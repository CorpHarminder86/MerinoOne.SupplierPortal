using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Application.Masters.UnitDecimals;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// R18 (2026-11-03) — a party answers the open negotiation round with a COUNTER-OFFER instead of accepting or rejecting it.
/// ONE command serves both sides (the buyer's <c>/counter</c> against a supplier round, the supplier's <c>/supplier-counter</c>
/// against a buyer round): <see cref="CounteredBy"/> is the party countering, and the round answered must have been authored by
/// the OTHER one (else 409), be Submitted (else 409) — and the number of rounds is unlimited.
///
/// <para><b>What a counter is.</b> The answered round (the "target") flips to <see cref="PoNegotiationStatus.Countered"/> and a NEW
/// round is opened: Submitted, <c>ProposedBy</c> = the countering party, <c>RoundNo</c> = target + 1, linked by
/// <c>ParentNegotiationId</c>. The PO stays in <see cref="PoStatus.Negotiation"/> and nothing is pushed to ERP — that happens when
/// a round is finally approved / accepted.</para>
///
/// <para><b>Values.</b> A line the request names overrides the target's EFFECTIVE value for it (its stored delta over the live PO
/// line); a line it omits keeps the target's. <b>Price is carried, never countered</b>: the request's price is ignored and the
/// target's effective price stands, so a counter cannot introduce a price change. Like every round, the new one stores deltas
/// against the LIVE PO line (original = the line as it is now, negotiated = the counter's value, only for lines that differ from
/// it), so the accepted round alone carries the whole proposal and the ERP payload needs no knowledge of earlier rounds.</para>
///
/// <para><b>Rules.</b> The create's per-line rules (<see cref="PoNegotiationLineRules"/>) judged against the TARGET's effective values: a
/// delivery that changed against the target may not predate the PO, a quantity that changed against it must fit its unit's
/// decimals, and an unknown PO line is a 400. A counter must change something the target proposes (else 400 — accept the round
/// instead) and must differ from the PO itself (else 400 — reject / decline instead). A supplier's counter is additionally
/// gated like its create: <c>AllowNegotiate</c> (409) and <see cref="SupplierWriteGuard"/> (403).</para>
///
/// <para><b>Persistence.</b> One transaction, two saves: the target is committed as Countered first, because the filtered unique
/// index <c>UX_PurchaseOrderNegotiation_po_open</c> allows ONE Submitted round per PO and a single SaveChanges is free to run
/// the INSERT before the UPDATE. The new round is owned by the TARGET's seccode (the supplier's G-seccode) whoever counters, so
/// the supplier keeps seeing the buyer's rounds through RLS. A concurrent answer to the same round is a 409 (RowVersion).</para>
/// </summary>
public record CounterPoNegotiationCommand(Guid Id, CounterPoNegotiationRequest Body, NegotiationParty CounteredBy)
    : IRequest<PoNegotiationDto>;

public class CounterPoNegotiationCommandValidator : AbstractValidator<CounterPoNegotiationCommand>
{
    public CounterPoNegotiationCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        this.AddProposalRules(x => x.Body.Notes, x => x.Body.Lines);
    }
}

public class CounterPoNegotiationCommandHandler : IRequestHandler<CounterPoNegotiationCommand, PoNegotiationDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;
    private readonly IUnitDecimalsResolver _units;

    public CounterPoNegotiationCommandHandler(
        IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard, IUnitDecimalsResolver units)
    {
        _db = db; _user = user; _guard = guard; _units = units;
    }

    public async Task<PoNegotiationDto> Handle(CounterPoNegotiationCommand request, CancellationToken ct)
    {
        var counterer = request.CounteredBy;
        var target = await PoNegotiationRounds.LoadOpenAsync(
            _db, request.Id, PoNegotiationRounds.Other(counterer), "countered", includeLines: true, ct);

        var po = await PoNegotiationRounds.LoadPoAsync(_db, target.PurchaseOrderId, includeLines: true, ct);
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == target.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", target.SupplierId);

        // A supplier's counter is a proposal like its create: the same AllowNegotiate gate and write authorization. The buyer
        // needs neither — mirrors its approve / reject.
        if (counterer == NegotiationParty.Supplier)
        {
            PoNegotiationRounds.EnsureSupplierMayNegotiate(supplier);
            await PoNegotiationRounds.EnsureSupplierCanWriteAsync(_guard, supplier.Id, po.SeccodeId, ct);
        }

        // Only after the authorization checks: an unauthorized caller costs no extra query (the unit lookup).
        var rules = await PoNegotiationLineRules.ForAsync(po, _units, ct);
        var now = DateTime.UtcNow;
        var actor = PoNegotiationRounds.ActorOf(_user);

        // What the target stands for, per PO line: the baseline this counter is judged against, and what a line it omits keeps.
        var answered = PoNegotiationTerms.Effective(rules.Lines.Select(l => (l.Id, PoLineTerms.Of(l))), target.Lines);

        // The counter's values: the target's, overridden by each line the request names. The price is NOT overridable — the
        // target's effective price carries, so a counter never introduces (or reverts) a price change.
        var proposal = new Dictionary<Guid, PoLineTerms>(answered);
        for (var index = 0; index < request.Body.Lines.Count; index++)
        {
            var input = request.Body.Lines[index];
            var poLine = rules.Line(input.PurchaseOrderLineId);

            var before = answered[poLine.Id];
            var proposed = before with { Qty = input.NegotiatedQty, DeliveryDate = input.NegotiatedDeliveryDate };
            rules.Judge(poLine, index, before, proposed);
            proposal[poLine.Id] = proposed;
        }

        if (proposal.All(p => p.Value == answered[p.Key]))
            throw PoNegotiationLineRules.LinesInvalid(
                "The counter changes nothing the round proposes — accept the round instead.");

        var round = new PurchaseOrderNegotiation
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = target.PurchaseOrderId,
            PoNumber = target.PoNumber,
            SupplierId = target.SupplierId,
            NegotiationStatus = PoNegotiationStatus.Submitted,
            ParentNegotiationId = target.Id,
            RoundNo = target.RoundNo + 1,
            ProposedBy = counterer,
            PreviousPoStatus = target.PreviousPoStatus,   // the revert target stays the status from before the negotiation began.
            SubmittedAt = now,
            Notes = PoNegotiationRounds.NotesOf(request.Body.Notes),
            // Owner = the TARGET's: the supplier's G-seccode, whoever counters. The acting user's own scope is irrelevant (a buyer
            // holds no SecRight on it) and the supplier must keep seeing the buyer's rounds. The scope-stamp interceptor fills a
            // scope column only while it is UNSET, so these pre-set values survive a buyer's own active company / tenant.
            SeccodeId = target.SeccodeId,
            TenantId = target.TenantId,
            TenantEntityId = target.TenantEntityId,
            CreatedBy = actor,
            CreatedOn = now,
        };

        // Stored like every round: only the lines whose proposal differs from the LIVE PO line, original = the line as it is now.
        foreach (var poLine in rules.Lines.OrderBy(l => l.PositionNo).ThenBy(l => l.SequenceNo))
        {
            var terms = proposal[poLine.Id];
            if (terms != PoLineTerms.Of(poLine))
                round.Lines.Add(PoNegotiationLineRules.StoredLine(poLine, terms, actor, now));
        }

        if (round.Lines.Count == 0)
            throw PoNegotiationLineRules.LinesInvalid(
                $"The counter is identical to the purchase order — {(counterer == NegotiationParty.Buyer ? "reject" : "decline")} the round instead.");

        await using var tx = await _db.BeginTransactionAsync(ct);

        // 1. The answered round leaves Submitted — committed to the transaction BEFORE the counter is inserted (see the class doc).
        PoNegotiationRounds.Close(target, PoNegotiationStatus.Countered, actor, now);
        PoNegotiationHistory.RecordOutcome(
            _db, po, target, "countered", $"{counterer} countered — round {round.RoundNo} proposed.", actor, now);
        await PoNegotiationRounds.SaveAsync(_db, ct);

        // 2. The counter takes its place as the PO's one open round. The PO stays in Negotiation, so it is not touched.
        _db.PurchaseOrderNegotiations.Add(round);
        PoNegotiationHistory.RecordSubmitted(_db, po, round, actor, now);
        await _db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        return PoNegotiationMapper.ToDto(round, supplier.LegalName);
    }
}
