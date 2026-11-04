using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Queries;

/// <summary>
/// Full negotiation detail for the diff view: the header plus ONE line per line of the negotiation's PO. A line the
/// supplier actually changed carries its stored original → negotiated values (<c>IsChanged = true</c>); every other PO
/// line is returned untouched (original = negotiated = the PO line's current qty / delivery date / unit price,
/// <c>IsChanged = false</c>) so the reviewer sees the whole order, not just the delta. Every line also carries the PO
/// line's item description, order unit and that unit's decimal places (one batched lookup).
///
/// <para>The stored model is unchanged: negotiation lines are still persisted delta-only (see
/// <c>CreatePoNegotiationCommand</c>); the unchanged lines are synthesised here, on read, from the live PO lines.
/// Lines are loaded via the parent (<c>Include(n => n.Lines)</c>) — there is no root DbSet for negotiation lines.</para>
///
/// <para><b>Rounds (R18, 2026-11-03):</b> the id may be ANY round of a negotiation. Because every round stores its deltas against
/// the LIVE PO line, a line's <c>Negotiated*</c> values here are this round's whole effective proposal for it (what a counter
/// editor seeds from), and each line also carries the PARENT round's effective values (<c>PreviousRoundQty</c> /
/// <c>PreviousRoundDeliveryDate</c>; null on round 1) so the diff can show what the round moved from. The DTO names its round
/// (<c>RoundNo</c>, <c>ProposedBy</c>, <c>ParentNegotiationId</c>), the chain's latest round (<c>HeadNegotiationId</c> — itself
/// when it is the head, so a superseded round can link "superseded by round N") and the chain's header history (<c>Rounds</c>,
/// root → head). However many rounds there are, the chain costs ONE query.</para>
///
/// Mirrors <c>GetSupplierChangeRequestByIdQuery</c>: reviewers open any negotiation in their tenant (bypass
/// seccode/company, re-apply tenant); suppliers stay seccode-scoped (404 on someone else's negotiation). A round
/// the buyer authored shares the supplier's seccode, so the supplier reads it like its own.
/// </summary>
public record GetPoNegotiationByIdQuery(Guid Id) : IRequest<PoNegotiationDto>;

public class GetPoNegotiationByIdQueryHandler : IRequestHandler<GetPoNegotiationByIdQuery, PoNegotiationDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IUnitDecimalsResolver _units;
    public GetPoNegotiationByIdQueryHandler(IAppDbContext db, ICurrentUser user, IUnitDecimalsResolver units)
    {
        _db = db; _user = user; _units = units;
    }

    public async Task<PoNegotiationDto> Handle(GetPoNegotiationByIdQuery request, CancellationToken ct)
    {
        var reviewer = _user.IsAdmin || _user.IsManager || _user.HasPermission(Perm.PurchaseOrderApproveNegotiation);
        var negs = reviewer
            ? _db.PurchaseOrderNegotiations.IgnoreQueryFilters().Where(x => !x.IsDeleted && x.TenantId == _user.TenantId)
            : _db.PurchaseOrderNegotiations.AsQueryable();
        var supSet = reviewer
            ? _db.Suppliers.IgnoreQueryFilters().Where(s => !s.IsDeleted && s.TenantId == _user.TenantId)
            : _db.Suppliers.AsQueryable();

        var n = await negs
            .Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct)
            ?? throw new NotFoundException("PurchaseOrderNegotiation", request.Id);

        var supplierName = await supSet
            .Where(s => s.Id == n.SupplierId)
            .Select(s => s.LegalName)
            .FirstOrDefaultAsync(ct);

        var chain = await LoadChainAsync(negs, n, ct);

        // The parent round's stored lines, for the "previous round" columns. A line carries no seccode of its own, and the
        // parent belongs to the chain just read through the same visibility check, so reading them directly widens nothing.
        var parentLines = n.ParentNegotiationId is { } parentId
            ? await _db.PurchaseOrderNegotiationLines
                .Where(l => l.PurchaseOrderNegotiationId == parentId && !l.IsDeleted)
                .ToListAsync(ct)
            : null;

        // The mapper yields the header plus the stored (delta) lines; widen those to the PO's full line set.
        var dto = PoNegotiationMapper.ToDto(n, supplierName ?? string.Empty);
        return dto with
        {
            Lines = await WidenToAllPoLinesAsync(n.PurchaseOrderId, dto.Lines, parentLines, ct),
            HeadNegotiationId = chain[^1].Id,
            Rounds = chain,
        };
    }

    /// <summary>
    /// The round history of the chain <paramref name="round"/> belongs to, root → head (so ascending RoundNo). A PO can carry several
    /// chains — a rejected or withdrawn negotiation is followed by a fresh one — and a chain has no id of its own, so ONE query
    /// reads the header columns of every round of the PO and the chain is found by walking the parent links in memory: no query
    /// per round. A round has at most one child (a counter flips the round it answers out of Submitted, and a concurrent second
    /// counter loses the RowVersion race), so the walk down from the root is a straight line.
    /// </summary>
    private static async Task<List<PoNegotiationRoundDto>> LoadChainAsync(
        IQueryable<PurchaseOrderNegotiation> negs, PurchaseOrderNegotiation round, CancellationToken ct)
    {
        var rows = await negs
            .Where(x => x.PurchaseOrderId == round.PurchaseOrderId)
            .Select(x => new RoundRow(
                x.ParentNegotiationId,
                new PoNegotiationRoundDto(
                    x.Id, x.RoundNo, x.ProposedBy.ToString(), x.NegotiationStatus.ToString(), x.SubmittedAt, x.CreatedBy,
                    x.ReviewedAt, x.ReviewedBy, x.Notes, x.RejectionReason)))
            .ToListAsync(ct);

        var byId = rows.ToDictionary(r => r.Round.Id);
        var childOf = rows.Where(r => r.ParentId is not null).ToDictionary(r => r.ParentId!.Value);

        var root = byId[round.Id];
        while (root.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent)) root = parent;

        var chain = new List<PoNegotiationRoundDto> { root.Round };
        while (childOf.TryGetValue(chain[^1].Id, out var child)) chain.Add(child.Round);
        return chain;
    }

    /// <summary>
    /// Returns one DTO per PO line (plus any stored line whose PO line has since been removed), ordered by
    /// PositionNo then SequenceNo. A stored line keeps its persisted values and is matched to its PO line by
    /// <c>PurchaseOrderLineId</c>; a PO line with no stored line is emitted unchanged. When the round has a parent
    /// (<paramref name="parentLines"/> non-null) every line also carries the parent round's effective qty / delivery date.
    ///
    /// <para>The PO lines are read directly rather than through the PO: a line carries no seccode of its own (the
    /// soft-delete filter is all that applies), and the negotiation above already passed its own visibility check —
    /// it shares its owner with the PO — so reading its lines widens nothing the caller could not already see.</para>
    /// </summary>
    private async Task<List<PoNegotiationLineDto>> WidenToAllPoLinesAsync(
        Guid purchaseOrderId, List<PoNegotiationLineDto> storedLines, List<PurchaseOrderNegotiationLine>? parentLines,
        CancellationToken ct)
    {
        var poLines = await _db.PurchaseOrderLines
            .Where(l => l.PurchaseOrderId == purchaseOrderId)
            .Select(l => new PoLineRow(
                l.Id, l.PositionNo, l.SequenceNo, l.ItemCode, l.ItemDescription, l.OrderUnit,
                l.OrderQty, l.DeliveryDate, l.PriceUnit))
            .ToListAsync(ct);

        // ONE unit lookup for the whole page, however many lines or units there are.
        var places = await _units.ResolveAsync(poLines.Select(l => (string?)l.OrderUnit), ct);
        int PlacesFor(string? unit)
            => !string.IsNullOrWhiteSpace(unit) && places.TryGetValue(unit, out var p) ? p : _units.Fallback;

        // What the PARENT round stood for per line: its stored lines over the live ones. Null on round 1.
        var previous = parentLines is null
            ? null
            : PoNegotiationTerms.Effective(
                poLines.Select(l => (l.Id, new PoLineTerms(l.OrderQty, l.DeliveryDate, l.PriceUnit))), parentLines);
        PoLineTerms? PreviousOf(Guid lineId)
            => previous is not null && previous.TryGetValue(lineId, out var terms) ? terms : null;

        var byId = poLines.ToDictionary(l => l.Id);
        var result = new List<PoNegotiationLineDto>(poLines.Count + storedLines.Count);

        foreach (var stored in storedLines)
        {
            byId.TryGetValue(stored.PurchaseOrderLineId, out var poLine);
            result.Add(stored with
            {
                ItemDescription = poLine?.ItemDescription,
                OrderUnit = poLine?.OrderUnit,
                UnitDecimalPlaces = PlacesFor(poLine?.OrderUnit),
                IsChanged = true,
                PreviousRoundQty = PreviousOf(stored.PurchaseOrderLineId)?.Qty,
                PreviousRoundDeliveryDate = PreviousOf(stored.PurchaseOrderLineId)?.DeliveryDate,
            });
        }

        var storedIds = storedLines.Select(l => l.PurchaseOrderLineId).ToHashSet();
        foreach (var l in poLines.Where(l => !storedIds.Contains(l.Id)))
        {
            result.Add(new PoNegotiationLineDto(
                l.Id, l.PositionNo, l.SequenceNo, l.ItemCode,
                OriginalQty: l.OrderQty, NegotiatedQty: l.OrderQty,
                OriginalDeliveryDate: l.DeliveryDate, NegotiatedDeliveryDate: l.DeliveryDate,
                OriginalPrice: l.PriceUnit, NegotiatedPrice: l.PriceUnit,
                ItemDescription: l.ItemDescription, OrderUnit: l.OrderUnit,
                UnitDecimalPlaces: PlacesFor(l.OrderUnit), IsChanged: false,
                PreviousRoundQty: PreviousOf(l.Id)?.Qty,
                PreviousRoundDeliveryDate: PreviousOf(l.Id)?.DeliveryDate));
        }

        return result.OrderBy(l => l.PositionNo).ThenBy(l => l.SequenceNo).ToList();
    }

    /// <summary>The slice of a PO line the widened view needs.</summary>
    private sealed record PoLineRow(
        Guid Id, int PositionNo, int SequenceNo, string ItemCode, string? ItemDescription, string? OrderUnit,
        decimal OrderQty, DateTime? DeliveryDate, decimal PriceUnit);

    /// <summary>A round's header row plus the parent link the chain walk follows (not part of the DTO).</summary>
    private sealed record RoundRow(Guid? ParentId, PoNegotiationRoundDto Round);
}
