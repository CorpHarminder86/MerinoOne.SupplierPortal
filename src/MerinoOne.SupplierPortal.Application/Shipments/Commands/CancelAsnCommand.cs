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

        // The status check below is the single-cancel guard: a Cancelled ASN is rejected here, so the cumulative
        // reversal can only ever run once per ASN (UC-ASN-11 — never double-decrement).
        if (asn.AsnStatus is not (AsnStatus.Draft or AsnStatus.Submitted))
            throw new ConflictException($"ASN is '{asn.AsnStatus}'; only Draft or Submitted ASNs can be cancelled.");

        var now = DateTime.UtcNow;

        // R4 (2026-06-26) — Addendum §4.4 / UC-ASN-11 — cumulative reversal. When the ASN flips to Cancelled, restore
        // each covered PO line's balance by decrementing ShippedQtyToDate by Σ(this ASN's line ShippedQty) for that
        // PO line. ALWAYS (independent of the D3 over-ship flag — the cumulative is always maintained). One atomic
        // ExecuteUpdateAsync per PO line — never a C# read-then-write on the cumulative (DI-02). Sum first so a PO
        // line shipped on more than one of this ASN's lines is reversed once by its total.
        var reversalByPoLine = await _db.AsnLines
            .Where(l => l.AsnId == asn.Id && !l.IsDeleted)
            .GroupBy(l => l.PurchaseOrderLineId)
            .Select(g => new { PoLineId = g.Key, Qty = g.Sum(x => x.ShippedQty) })
            .ToListAsync(ct);

        await using var tx = await _db.BeginTransactionAsync(ct);

        foreach (var r in reversalByPoLine)
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
}
