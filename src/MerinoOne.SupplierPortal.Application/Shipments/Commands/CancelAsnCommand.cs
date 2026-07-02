using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 3. Cancels an ASN (Draft or Submitted -> Cancelled). Terminal for supplier edits
/// (Q2: no un-submit; Cancel is the only exit once Submitted). A Cancelled ASN's auto-created draft invoice is
/// left as-is (it is portal-internal and never auto-posted; admin handles it separately).
/// </summary>
public record CancelAsnCommand(Guid Id) : IRequest<Unit>;

public class CancelAsnCommandValidator : AbstractValidator<CancelAsnCommand>
{
    public CancelAsnCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class CancelAsnCommandHandler : IRequestHandler<CancelAsnCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CancelAsnCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<Unit> Handle(CancelAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        // R5 (§10.1) — any active (non-terminal) ASN may be cancelled; a Cancelled/Delivered ASN is rejected here,
        // so the cumulative reversal (when applicable) can only ever run once per ASN (UC-ASN-11 — never double).
        AsnLifecycle.AssertCanCancel(asn.AsnStatus);

        var now = DateTime.UtcNow;

        // R5 (TSD R5 Addendum §10.4) — balance is consumed ONLY at final Submit, so ONLY a previously-consumed ASN
        // (Submitted / InTransit) needs a reversal on cancel. A Draft / PendingApproval / Rejected ASN never touched
        // shippedQtyToDate, so cancelling it needs NO reversal. The reversal (when it runs) restores each covered PO
        // line's balance by Σ(this ASN's line ShippedQty), as in R4 §4.4 — one atomic ExecuteUpdateAsync per line.
        var consumedBalance = asn.AsnStatus is AsnStatus.Submitted or AsnStatus.InTransit;

        if (consumedBalance)
        {
            var reversalByPoLine = await _db.AsnLines
                .Where(l => l.AsnId == asn.Id && !l.IsDeleted)
                .GroupBy(l => l.PurchaseOrderLineId)
                .Select(g => new { PoLineId = g.Key, Qty = g.Sum(x => x.ShippedQty) })
                .ToListAsync(ct);

            await using var tx = await _db.BeginTransactionAsync(ct);

            // DI-02 — lock ordering: reverse in ascending PurchaseOrderLineId, matching the acquisition loops,
            // so concurrent multi-line cancel/submit never acquire the line X-locks in opposite orders.
            foreach (var r in reversalByPoLine.OrderBy(r => r.PoLineId))
            {
                await _db.PurchaseOrderLines
                    .Where(l => l.Id == r.PoLineId)
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        l => l.ShippedQtyToDate, l => l.ShippedQtyToDate - r.Qty), ct);
            }

            asn.AsnStatus = AsnStatus.Cancelled;
            asn.UpdatedBy = _user.UserCode;
            asn.UpdatedOn = now;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Unit.Value;
        }

        // No reversal needed — a single SaveChanges flips the status.
        asn.AsnStatus = AsnStatus.Cancelled;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = now;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
