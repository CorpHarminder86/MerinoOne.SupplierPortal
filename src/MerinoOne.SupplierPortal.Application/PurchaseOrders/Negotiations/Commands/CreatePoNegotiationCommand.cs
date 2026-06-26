using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;

/// <summary>
/// A supplier raises a PO negotiation (mirrors <c>CreateSupplierChangeRequestCommand</c>). The negotiation proposes
/// revised qty / delivery date on one or more PO lines and NEVER mutates the live PO lines — ERP stays the line
/// master. The PO must be Released or Acknowledged (else 409). Only lines whose qty OR delivery date actually
/// differ from the live line are persisted (delta); at least one must differ (else 400). The PO's current status
/// is captured into <c>PreviousPoStatus</c> (the revert target for cancel/reject) and the PO is flipped to
/// <see cref="PoStatus.Negotiation"/>. The negotiation aggregate's Owner is the supplier's G-seccode (seccode RLS).
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
        RuleFor(x => x.Body.Notes).MaximumLength(1000);
        RuleFor(x => x.Body.Lines).NotEmpty().WithMessage("At least one line is required.");
        RuleForEach(x => x.Body.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PurchaseOrderLineId).NotEmpty();
            line.RuleFor(l => l.NegotiatedQty).GreaterThan(0).WithMessage("Negotiated quantity must be greater than zero.");
            line.RuleFor(l => l.NegotiatedPrice).GreaterThanOrEqualTo(0).WithMessage("Negotiated price cannot be negative.");
        });
    }
}

public class CreatePoNegotiationCommandHandler : IRequestHandler<CreatePoNegotiationCommand, PoNegotiationDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public CreatePoNegotiationCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<PoNegotiationDto> Handle(CreatePoNegotiationCommand request, CancellationToken ct)
    {
        var po = await _db.PurchaseOrders
            .Include(p => p.Lines)
            .FirstOrDefaultAsync(p => p.Id == request.Body.PurchaseOrderId, ct)
            ?? throw new NotFoundException("PurchaseOrder", request.Body.PurchaseOrderId);

        // Eligible only while the PO is open for a response (mirrors the AcknowledgeBefore/Accept gate).
        if (po.PoStatus is not (PoStatus.Released or PoStatus.Acknowledged))
            throw new ConflictException(
                $"A negotiation can only be raised on a Released or Acknowledged PO (current: {po.PoStatus}).");

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == po.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", po.SupplierId);

        // R4 (2026-06-26) — D1: the negotiate entry-point is gated on the supplier's AllowNegotiate toggle. A
        // supplier configured no-negotiate cannot raise a counter-proposal (UC-PO-04). 409 (conflicting config),
        // mirrors the AllowReject gate on /reject.
        if (!supplier.AllowNegotiate)
            throw new ConflictException("This supplier is not permitted to negotiate purchase orders.");

        // Permission-based supplier write-path authorization (see XML doc). Throws ForbiddenException (403).
        await _guard.EnsureCanWriteAsync(supplier.Id, po.SeccodeId, "PurchaseOrder.Negotiate", ct);

        var now = DateTime.UtcNow;
        var actor = Actor();

        var negotiation = new PurchaseOrderNegotiation
        {
            Id = Guid.NewGuid(),
            PurchaseOrderId = po.Id,
            PoNumber = po.PoNumber,
            SupplierId = po.SupplierId,
            NegotiationStatus = PoNegotiationStatus.Submitted,
            PreviousPoStatus = po.PoStatus,           // revert target for cancel/reject (never hardcode Acknowledged).
            SubmittedAt = now,
            Notes = string.IsNullOrWhiteSpace(request.Body.Notes) ? null : request.Body.Notes.Trim(),
            SeccodeId = po.SeccodeId,                 // Owner = PO/supplier G-seccode (seccode RLS).
            TenantId = po.TenantId,
            TenantEntityId = po.TenantEntityId,
            CreatedBy = actor,
            CreatedOn = now,
        };

        // Build only the CHANGED delta lines (qty OR delivery date differs from the live PO line), snapshotting the
        // original. The live PO lines are NOT mutated — ERP stays the line master.
        foreach (var input in request.Body.Lines)
        {
            var poLine = po.Lines.FirstOrDefault(l => l.Id == input.PurchaseOrderLineId && !l.IsDeleted)
                         ?? throw new ValidationException(new Dictionary<string, string[]>
                         {
                             ["lines"] = new[] { $"PO line {input.PurchaseOrderLineId} does not belong to this PO." }
                         });

            var qtyChanged = poLine.OrderQty != input.NegotiatedQty;
            var dateChanged = poLine.DeliveryDate != input.NegotiatedDeliveryDate;
            var priceChanged = poLine.PriceUnit != input.NegotiatedPrice;
            if (!qtyChanged && !dateChanged && !priceChanged) continue;   // no-op line — drop it.

            negotiation.Lines.Add(new PurchaseOrderNegotiationLine
            {
                Id = Guid.NewGuid(),
                PurchaseOrderLineId = poLine.Id,
                PositionNo = poLine.PositionNo,
                SequenceNo = poLine.SequenceNo,
                ItemCode = poLine.ItemCode,
                OriginalQty = poLine.OrderQty,
                NegotiatedQty = input.NegotiatedQty,
                OriginalDeliveryDate = poLine.DeliveryDate,
                NegotiatedDeliveryDate = input.NegotiatedDeliveryDate,
                OriginalPrice = poLine.PriceUnit,
                NegotiatedPrice = input.NegotiatedPrice,
                CreatedBy = actor,
                CreatedOn = now,
            });
        }

        if (negotiation.Lines.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "No line differs from the purchase order — nothing to negotiate." }
            });

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

    private string Actor() => string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;
}
