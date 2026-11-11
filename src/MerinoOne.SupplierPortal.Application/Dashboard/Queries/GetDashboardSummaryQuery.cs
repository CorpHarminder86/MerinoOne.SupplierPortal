using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Dashboard;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
// R5: DeliveryScheduleStatus is in the same namespace as ScheduleStatus — no extra using needed.

namespace MerinoOne.SupplierPortal.Application.Dashboard.Queries;

/// <summary>
/// Dashboard KPI + recent-activity payload (TSD §11). Uses <see cref="IAppDbContext"/> directly
/// so the global seccode filter scopes counts per user.
/// </summary>
public record GetDashboardSummaryQuery() : IRequest<DashboardSummaryDto>;

public class GetDashboardSummaryQueryHandler : IRequestHandler<GetDashboardSummaryQuery, DashboardSummaryDto>
{
    private readonly IAppDbContext _db;
    public GetDashboardSummaryQueryHandler(IAppDbContext db) => _db = db;

    public async Task<DashboardSummaryDto> Handle(GetDashboardSummaryQuery request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var last30 = now.AddDays(-30);
        var last60 = now.AddDays(-60);

        // ---- KPI counts (current period) ----------------------------------------
        // EF can't translate `staticArray.Contains(enumProperty)` when the enum has a string
        // conversion configured, so we expand the predicates with explicit OR-chains.
        var activeSuppliers = await _db.Suppliers.CountAsync(s => s.IsActiveSupplier, ct);
        var openPos = await _db.PurchaseOrders.CountAsync(p =>
            p.PoStatus == PoStatus.Released
            || p.PoStatus == PoStatus.Acknowledged
            || p.PoStatus == PoStatus.Accepted
            || p.PoStatus == PoStatus.PartiallyDelivered, ct);
        // "Submitted" = submitted and still awaiting a decision. Submit runs the match straight away and lands the
        // invoice in Matched or MatchExceptions (it rarely rests in Submitted), so those count too; Approved /
        // Rejected / Paid / Cancelled have left the queue and Draft was never submitted.
        var submittedInvoices30 = await _db.Invoices.CountAsync(i =>
            (i.InvoiceStatus == InvoiceStatus.Submitted
             || i.InvoiceStatus == InvoiceStatus.UnderReview
             || i.InvoiceStatus == InvoiceStatus.Matched
             || i.InvoiceStatus == InvoiceStatus.MatchExceptions)
            && i.CreatedOn > last30, ct);
        var payments30Net = await _db.Payments
            .Where(p => p.PaymentDate > last30)
            .SumAsync(p => (decimal?)p.NetPaid, ct) ?? 0m;
        // R5 (TSD R5 Addendum §4.4): DeliverySchedule is always Approved at creation. "Open" = the schedule's PO line
        // still has something left to ship (orderQty − shippedQtyToDate > 0) — the same expression the Delivery
        // Schedules screen's "only with balance" filter uses, so the tile matches that list.
        var openSchedules = await (
            from d in _db.DeliverySchedules
            join line in _db.PurchaseOrderLines on d.PurchaseOrderLineId equals line.Id
            where d.Status == DeliveryScheduleStatus.Approved
                  && line.OrderQty - line.ShippedQtyToDate > 0
            select d.Id).CountAsync(ct);

        // ---- previous-period values (30..60 days ago) ---------------------------
        var submittedInvoicesPrev = await _db.Invoices.CountAsync(i =>
            (i.InvoiceStatus == InvoiceStatus.Submitted
             || i.InvoiceStatus == InvoiceStatus.UnderReview
             || i.InvoiceStatus == InvoiceStatus.Matched
             || i.InvoiceStatus == InvoiceStatus.MatchExceptions)
            && i.CreatedOn > last60 && i.CreatedOn <= last30, ct);
        var paymentsPrevNet = await _db.Payments
            .Where(p => p.PaymentDate > last60 && p.PaymentDate <= last30)
            .SumAsync(p => (decimal?)p.NetPaid, ct) ?? 0m;

        var kpis = new List<DashboardKpiDto>
        {
            new("Active suppliers",          activeSuppliers,            null,                       Sign(activeSuppliers, null)),
            new("Open POs",                  openPos,                    null,                       Sign(openPos, null)),
            new("Submitted invoices (30d)",  submittedInvoices30,        submittedInvoicesPrev,      Sign(submittedInvoices30, submittedInvoicesPrev)),
            new("Payments (30d)",            payments30Net,              paymentsPrevNet,            Sign(payments30Net, paymentsPrevNet)),
            new("Open delivery schedules",   openSchedules,              null,                       Sign(openSchedules, null)),
        };

        // ---- recent activity (TOP 10 across PO / ASN / Invoice / Payment) -------
        var recentPos = await _db.PurchaseOrders
            .OrderByDescending(p => p.CreatedOn).Take(10)
            .Select(p => new DashboardActivityDto("PurchaseOrder", p.PoNumber, p.PoStatus.ToString(), p.CreatedOn))
            .ToListAsync(ct);
        var recentAsns = await _db.Asns
            .OrderByDescending(a => a.CreatedOn).Take(10)
            .Select(a => new DashboardActivityDto("Asn", a.AsnNumber, a.AsnStatus.ToString(), a.CreatedOn))
            .ToListAsync(ct);
        var recentInvoices = await _db.Invoices
            .OrderByDescending(i => i.CreatedOn).Take(10)
            .Select(i => new DashboardActivityDto("Invoice", i.InvoiceNumber, i.InvoiceStatus.ToString(), i.CreatedOn))
            .ToListAsync(ct);
        var recentPayments = await _db.Payments
            .OrderByDescending(p => p.CreatedOn).Take(10)
            .Select(p => new DashboardActivityDto("Payment", p.PaymentReference, p.NetPaid > 0 ? "Paid" : "Pending", p.CreatedOn))
            .ToListAsync(ct);

        var recent = recentPos.Concat(recentAsns).Concat(recentInvoices).Concat(recentPayments)
            .OrderByDescending(a => a.When)
            .Take(10)
            .ToList();

        return new DashboardSummaryDto(kpis, recent);
    }

    private static string? Sign(decimal current, decimal? previous)
    {
        if (!previous.HasValue || previous.Value == 0m) return null;
        var delta = current - previous.Value;
        if (delta > 0m) return "+";
        if (delta < 0m) return "-";
        return null;
    }
}
