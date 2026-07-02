using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Application.Invoices;

/// <summary>
/// R6 (2026-07-02, plan D8) — compensating release of the per-PO-line invoiced-qty reservation taken at invoice
/// submit. The reservation (<c>PurchaseOrderLine.invoicedQtyToDate += billed</c>) is HELD while the invoice is in
/// any of <see cref="HoldsReservation"/>'s states and RELEASED when the invoice leaves that set via Revoke
/// (→ Draft) or Reject (→ Rejected).
///
/// <para>Release mirrors the acquisition's atomicity: a NEGATIVE conditional <c>ExecuteUpdateAsync</c> per PO line
/// (<c>WHERE invoicedQtyToDate ≥ qty</c>) so the cumulative can never go negative. If the conditional affects 0
/// rows (data drift — e.g. a manual correction shrank the cumulative below this invoice's share) the release
/// CLAMPS the line to 0 and logs a warning — never negative, never a throw. Callers run this INSIDE their own
/// transaction so a later SaveChanges failure rolls the releases back too.</para>
/// </summary>
public static class InvoiceReservationRelease
{
    /// <summary>The reservation-holding status set (plan D8). Draft / Rejected / Cancelled hold nothing.</summary>
    public static bool HoldsReservation(InvoiceStatus status) => status
        is InvoiceStatus.Submitted
        or InvoiceStatus.UnderReview
        or InvoiceStatus.Matched
        or InvoiceStatus.MatchExceptions
        or InvoiceStatus.Approved
        or InvoiceStatus.Paid
        or InvoiceStatus.PartiallyPaid;

    /// <summary>
    /// Subtracts this invoice's billed quantities (aggregated per PO line) from the lines' cumulative
    /// <c>invoicedQtyToDate</c>. MUST run inside the caller's transaction (the conditional updates execute
    /// immediately, not on SaveChanges).
    /// </summary>
    public static async Task ReleaseAsync(IAppDbContext db, Guid invoiceId, ILogger logger, CancellationToken ct)
    {
        var perPoLine = await db.InvoiceLines
            .Where(l => l.InvoiceId == invoiceId)
            .GroupBy(l => l.PurchaseOrderLineId)
            .Select(g => new { PoLineId = g.Key, Qty = g.Sum(x => x.BilledQty) })
            .ToListAsync(ct);

        foreach (var x in perPoLine.Where(x => x.Qty > 0))
        {
            var affected = await db.PurchaseOrderLines
                .Where(l => l.Id == x.PoLineId && l.InvoicedQtyToDate >= x.Qty)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    l => l.InvoicedQtyToDate, l => l.InvoicedQtyToDate - x.Qty), ct);

            if (affected == 0)
            {
                // Floor at 0 — the cumulative drifted below this invoice's share; clamp + log, never negative.
                logger.LogWarning(
                    "Invoice reservation release clamped PO line {PoLineId} to 0 (release qty {Qty} exceeded the " +
                    "current invoicedQtyToDate) — invoice {InvoiceId}.",
                    x.PoLineId, x.Qty, invoiceId);
                await db.PurchaseOrderLines
                    .Where(l => l.Id == x.PoLineId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.InvoicedQtyToDate, 0m), ct);
            }
        }
    }
}
