using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
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
/// A supplier raises a PO negotiation (mirrors <c>CreateSupplierChangeRequestCommand</c>). The negotiation proposes
/// revised qty / delivery date on one or more PO lines and NEVER mutates the live PO lines — ERP stays the line
/// master. The PO must be Released or Acknowledged (else 409). Only lines whose qty OR delivery date actually
/// differ from the live line are persisted (delta); at least one must differ (else 400). The PO's current status
/// is captured into <c>PreviousPoStatus</c> (the revert target for cancel/reject) and the PO is flipped to
/// <see cref="PoStatus.Negotiation"/>. The negotiation aggregate's Owner is the supplier's G-seccode (seccode RLS).
///
/// <para><b>Rounds (R18):</b> this is round 1 of a negotiation, proposed by the supplier. The buyer may counter it
/// (<see cref="CounterPoNegotiationCommand"/>) and the supplier may counter back, any number of times.</para>
///
/// <para><b>Per-line rules:</b> a PROPOSED delivery instant may not predate the PO, and a PROPOSED quantity may not carry
/// more decimal places than the line's unit of measure allows (<c>mdm.Unit.decimalPlaces</c>; an unknown or blank unit
/// allows 3) — else 400. A value the supplier did not change is never judged. The rules are shared with the counter-offer
/// commands: <see cref="PoNegotiationLineRules"/>.</para>
///
/// <para><b>Supplier write path:</b> a supplier user has <c>SecRight.canWrite=false</c> on its G-seccode. The
/// write of THIS portal-originated aggregate is authorized through <see cref="SupplierWriteGuard"/> with the
/// <c>PurchaseOrder.Negotiate</c> self-service permission + a verified <c>SupplierUserMap</c> membership — the
/// global RLS write rule is not weakened (the supplier still cannot write the live PO/line rows directly).</para>
/// </summary>
public record CreatePoNegotiationCommand(CreatePoNegotiationRequest Body) : IRequest<PoNegotiationDto>;

public class CreatePoNegotiationCommandValidator : AbstractValidator<CreatePoNegotiationCommand>
{
    public CreatePoNegotiationCommandValidator()
    {
        RuleFor(x => x.Body.PurchaseOrderId).NotEmpty();
        this.AddProposalRules(x => x.Body.Notes, x => x.Body.Lines);
    }
}

public class CreatePoNegotiationCommandHandler : IRequestHandler<CreatePoNegotiationCommand, PoNegotiationDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;
    private readonly IUnitDecimalsResolver _units;

    public CreatePoNegotiationCommandHandler(
        IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard, IUnitDecimalsResolver units)
    {
        _db = db; _user = user; _guard = guard; _units = units;
    }

    public async Task<PoNegotiationDto> Handle(CreatePoNegotiationCommand request, CancellationToken ct)
    {
        var po = await PoNegotiationRounds.LoadPoAsync(_db, request.Body.PurchaseOrderId, includeLines: true, ct);

        // Eligible only while the PO is open for a response (mirrors the AcknowledgeBefore/Accept gate).
        if (po.PoStatus is not (PoStatus.Released or PoStatus.Acknowledged))
            throw new ConflictException(
                $"A negotiation can only be raised on a Released or Acknowledged PO (current: {po.PoStatus}).");

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == po.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", po.SupplierId);

        PoNegotiationRounds.EnsureSupplierMayNegotiate(supplier);

        // Permission-based supplier write-path authorization (see XML doc). Throws ForbiddenException (403).
        await PoNegotiationRounds.EnsureSupplierCanWriteAsync(_guard, supplier.Id, po.SeccodeId, ct);

        // Only after the authorization check: an unauthorized caller costs no extra query (the unit lookup).
        var rules = await PoNegotiationLineRules.ForAsync(po, _units, ct);

        var now = DateTime.UtcNow;
        var actor = PoNegotiationRounds.ActorOf(_user);

        var negotiation = new PurchaseOrderNegotiation
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = po.Id,
            PoNumber = po.PoNumber,
            SupplierId = po.SupplierId,
            NegotiationStatus = PoNegotiationStatus.Submitted,
            PreviousPoStatus = po.PoStatus,           // revert target for cancel/reject (never hardcode Acknowledged).
            SubmittedAt = now,
            Notes = PoNegotiationRounds.NotesOf(request.Body.Notes),
            SeccodeId = po.SeccodeId,                 // Owner = PO/supplier G-seccode (seccode RLS).
            TenantId = po.TenantId,
            TenantEntityId = po.TenantEntityId,
            CreatedBy = actor,
            CreatedOn = now,
        };

        // Build only the CHANGED delta lines (qty OR delivery date differs from the live PO line), snapshotting the
        // original. The live PO lines are NOT mutated — ERP stays the line master.
        for (var index = 0; index < request.Body.Lines.Count; index++)
        {
            var input = request.Body.Lines[index];
            var poLine = rules.Line(input.PurchaseOrderLineId);

            var live = PoLineTerms.Of(poLine);
            var proposed = new PoLineTerms(input.NegotiatedQty, input.NegotiatedDeliveryDate, input.NegotiatedPrice);
            rules.Judge(poLine, index, live, proposed);
            if (proposed == live) continue;   // no-op line — drop it.

            negotiation.Lines.Add(PoNegotiationLineRules.StoredLine(poLine, proposed, actor, now));
        }

        if (negotiation.Lines.Count == 0)
            throw PoNegotiationLineRules.LinesInvalid("No line differs from the purchase order — nothing to negotiate.");

        // Flip the PO to Negotiation in the SAME transaction as the negotiation insert.
        po.PoStatus = PoStatus.Negotiation;
        po.UpdatedBy = actor;
        po.UpdatedOn = now;

        _db.PurchaseOrderNegotiations.Add(negotiation);

        // Surface the proposed per-line qty / delivery changes on the PO "History" tab (PO-targeted audit rows).
        PoNegotiationHistory.RecordSubmitted(_db, po, negotiation, actor, now);

        await _db.SaveChangesAsync(ct);

        return PoNegotiationMapper.ToDto(negotiation, supplier.LegalName);
    }
}
