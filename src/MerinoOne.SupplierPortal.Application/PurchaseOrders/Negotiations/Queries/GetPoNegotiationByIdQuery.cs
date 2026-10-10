using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
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
/// Mirrors <c>GetSupplierChangeRequestByIdQuery</c>: reviewers open any negotiation in their tenant (bypass
/// seccode/company, re-apply tenant); suppliers stay seccode-scoped (404 on someone else's negotiation).
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

        // The mapper yields the header plus the stored (delta) lines; widen those to the PO's full line set.
        var dto = PoNegotiationMapper.ToDto(n, supplierName ?? string.Empty);
        return dto with { Lines = await WidenToAllPoLinesAsync(n.PurchaseOrderId, dto.Lines, ct) };
    }

    /// <summary>
    /// Returns one DTO per PO line (plus any stored line whose PO line has since been removed), ordered by
    /// PositionNo then SequenceNo. A stored line keeps its persisted values and is matched to its PO line by
    /// <c>PurchaseOrderLineId</c>; a PO line with no stored line is emitted unchanged.
    ///
    /// <para>The PO lines are read directly rather than through the PO: a line carries no seccode of its own (the
    /// soft-delete filter is all that applies), and the negotiation above already passed its own visibility check —
    /// it shares its owner with the PO — so reading its lines widens nothing the caller could not already see.</para>
    /// </summary>
    private async Task<List<PoNegotiationLineDto>> WidenToAllPoLinesAsync(
        Guid purchaseOrderId, List<PoNegotiationLineDto> storedLines, CancellationToken ct)
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
                UnitDecimalPlaces: PlacesFor(l.OrderUnit), IsChanged: false));
        }

        return result.OrderBy(l => l.PositionNo).ThenBy(l => l.SequenceNo).ToList();
    }

    /// <summary>The slice of a PO line the widened view needs.</summary>
    private sealed record PoLineRow(
        Guid Id, int PositionNo, int SequenceNo, string ItemCode, string? ItemDescription, string? OrderUnit,
        decimal OrderQty, DateTime? DeliveryDate, decimal PriceUnit);
}
