using System.Security;
using Dapper;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Payments;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Payments.Queries;

/// <summary>
/// Enhancement R4 — Module 7 (Payment Summary). Paged projection, one row per invoice, 10 spec columns.
/// <para>Two execution paths (plan completeness #28 — data-leak guard):</para>
/// <list type="bullet">
///   <item><b>Admin / Manager</b>: a Dapper join (no seccode overhead) — they see every supplier's financials.</item>
///   <item><b>Supplier (non-privileged)</b>: the EF path against <see cref="IAppDbContext"/>, so the global
///         seccode + tenant/company filters scope rows to the caller's own invoices.</item>
/// </list>
/// The handler <b>hard-asserts <c>_user.IsAdmin || _user.IsManager</c> before taking the Dapper branch</b>;
/// a supplier principal can NEVER reach the no-seccode query.
/// <para>Column derivation:</para>
/// <list type="bullet">
///   <item><c>ReceivedAmount</c> = Σ Payment.NetPaid for the invoice; <c>BalanceToReceive</c> = NetAmount − ReceivedAmount.</item>
///   <item><c>PaymentDueDate</c> = InvoiceDate + PaymentTerm.NetDays, using the PO's term, fall back to the supplier's term.</item>
///   <item><c>GrnNumber/GrnDate</c> = the latest-approved covering GRN; <c>GrnCount</c> = count of linked GRNs.</item>
///   <item><c>IssueReported</c> = a non-empty GoodsReceipt.IssueReported across the covering GRNs.</item>
/// </list>
/// </summary>
public record GetPaymentSummaryQuery(
    int Page = 1,
    int PageSize = 50,
    Guid? SupplierId = null,
    DateTime? From = null,
    DateTime? To = null,
    string? Status = null) : IRequest<PagedResult<PaymentSummaryRowDto>>;

public class GetPaymentSummaryQueryHandler : IRequestHandler<GetPaymentSummaryQuery, PagedResult<PaymentSummaryRowDto>>
{
    private readonly IAppDbContext _db;
    private readonly ISqlConnectionFactory _sql;
    private readonly ICurrentUser _user;

    public GetPaymentSummaryQueryHandler(IAppDbContext db, ISqlConnectionFactory sql, ICurrentUser user)
    {
        _db = db;
        _sql = sql;
        _user = user;
    }

    public async Task<PagedResult<PaymentSummaryRowDto>> Handle(GetPaymentSummaryQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        // HARD GATE: only a privileged principal may take the no-seccode Dapper branch. A supplier
        // principal is forced onto the EF path where the global seccode filter scopes its own invoices.
        if (_user.IsAdmin || _user.IsManager)
            return await ViaDapperAsync(request, page, pageSize, ct);

        return await ViaEfAsync(request, page, pageSize, ct);
    }

    // --- privileged path (no seccode) --------------------------------------------

    private async Task<PagedResult<PaymentSummaryRowDto>> ViaDapperAsync(
        GetPaymentSummaryQuery request, int page, int pageSize, CancellationToken ct)
    {
        // Defence in depth: never allow a non-privileged principal to reach this branch.
        if (!(_user.IsAdmin || _user.IsManager))
            throw new SecurityException("Payment Summary Dapper path requires Admin or Manager.");

        // ReceivedAmount = Σ NetPaid; PaymentReference = latest payment's reference.
        // Latest-approved GRN + GRN count via correlated sub-selects keyed on GoodsReceipt.invoiceId.
        // PaymentDueDate = invoiceDate + COALESCE(PO term netDays, supplier term netDays).
        const string sql = @"
WITH src AS (
    SELECT
        i.invoiceId,
        i.invoiceNumber                                       AS InvoiceNumber,
        i.invoiceDate                                         AS InvoiceDate,
        i.netAmount                                           AS InvoiceAmount,
        i.invoiceStatus                                       AS InvoiceStatus,
        i.supplierId                                          AS SupplierId,
        ISNULL(pay.received, 0)                               AS ReceivedAmount,
        i.netAmount - ISNULL(pay.received, 0)                 AS BalanceToReceive,
        grn.grnNumber                                         AS GrnNumber,
        ISNULL(grn.grnApprovedAt, grn.grnDate)               AS GrnDate,
        ISNULL(grncnt.cnt, 0)                                 AS GrnCount,
        issue.issueReported                                   AS IssueReported,
        DATEADD(DAY, COALESCE(pt.netDays, st.netDays, 0), i.invoiceDate) AS PaymentDueDate,
        latestpay.paymentReference                            AS PaymentReference
    FROM [proc].[Invoice] i
    LEFT JOIN (
        SELECT p.invoiceId, SUM(p.netPaid) AS received
          FROM [proc].[Payment] p
         WHERE p.isDeleted = 0
         GROUP BY p.invoiceId
    ) pay ON pay.invoiceId = i.invoiceId
    OUTER APPLY (
        SELECT TOP (1) p.paymentReference
          FROM [proc].[Payment] p
         WHERE p.isDeleted = 0 AND p.invoiceId = i.invoiceId
         ORDER BY p.paymentDate DESC, p.paymentSeq DESC
    ) latestpay
    OUTER APPLY (
        SELECT TOP (1) g.grnNumber, g.grnDate, g.grnApprovedAt
          FROM [proc].[GoodsReceipt] g
         WHERE g.isDeleted = 0 AND g.invoiceId = i.invoiceId AND g.grnStatus = 'GrnApproved'
         ORDER BY g.grnApprovedAt DESC, g.grnDate DESC, g.goodsReceiptSeq DESC
    ) grn
    OUTER APPLY (
        SELECT COUNT(1) AS cnt
          FROM [proc].[GoodsReceipt] g
         WHERE g.isDeleted = 0 AND g.invoiceId = i.invoiceId
    ) grncnt
    OUTER APPLY (
        SELECT TOP (1) g.issueReported
          FROM [proc].[GoodsReceipt] g
         WHERE g.isDeleted = 0 AND g.invoiceId = i.invoiceId
           AND g.issueReported IS NOT NULL AND LEN(g.issueReported) > 0
         ORDER BY g.grnApprovedAt DESC, g.grnDate DESC, g.goodsReceiptSeq DESC
    ) issue
    LEFT JOIN [proc].[PaymentTerm] pt ON pt.paymentTermId = (
        SELECT po.paymentTermId FROM [proc].[PurchaseOrder] po WHERE po.purchaseOrderId = i.purchaseOrderId
    )
    LEFT JOIN [supplier].[Supplier] sup ON sup.supplierId = i.supplierId
    LEFT JOIN [proc].[PaymentTerm] st ON st.paymentTermId = sup.paymentTermId
    WHERE i.isDeleted = 0
      AND (@supplierId IS NULL OR i.supplierId = @supplierId)
      AND (@from IS NULL OR i.invoiceDate >= @from)
      AND (@to IS NULL OR i.invoiceDate <= @to)
      AND (@status IS NULL OR i.invoiceStatus = @status)
)
SELECT COUNT(1) FROM src;
SELECT InvoiceNumber, InvoiceDate, InvoiceAmount, GrnNumber, GrnDate, GrnCount,
       IssueReported, PaymentDueDate, PaymentReference, ReceivedAmount, BalanceToReceive
  FROM src
 ORDER BY InvoiceDate DESC, InvoiceNumber DESC
OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;";

        var p = new
        {
            supplierId = request.SupplierId,
            from = request.From,
            to = request.To,
            status = request.Status,
            offset = (page - 1) * pageSize,
            take = pageSize
        };

        await using var cn = await _sql.OpenAsync(ct);
        using var multi = await cn.QueryMultipleAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        var total = await multi.ReadFirstAsync<int>();
        var items = (await multi.ReadAsync<PaymentSummaryRowDto>()).AsList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<PaymentSummaryRowDto>(items, page, pageSize, total, totalPages);
    }

    // --- supplier / non-privileged path (seccode auto-applied) -------------------

    private async Task<PagedResult<PaymentSummaryRowDto>> ViaEfAsync(
        GetPaymentSummaryQuery request, int page, int pageSize, CancellationToken ct)
    {
        // EF query — the AppDbContext global seccode + tenant/company filters scope these DbSets to the
        // caller's own rows automatically. No raw SQL, no leak.
        var invoices = _db.Invoices.AsQueryable();

        if (request.SupplierId.HasValue)
            invoices = invoices.Where(i => i.SupplierId == request.SupplierId.Value);
        if (request.From.HasValue)
            invoices = invoices.Where(i => i.InvoiceDate >= request.From.Value);
        if (request.To.HasValue)
            invoices = invoices.Where(i => i.InvoiceDate <= request.To.Value);
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var status = request.Status.Trim();
            invoices = invoices.Where(i => i.InvoiceStatus.ToString() == status);
        }

        var total = await invoices.CountAsync(ct);

        // Project with correlated sub-queries. Each GoodsReceipt/Payment is also seccode-filtered, so a
        // supplier only ever sums/links its own GRNs and payments.
        var raw = await invoices
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.InvoiceNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new
            {
                i.InvoiceNumber,
                i.InvoiceDate,
                i.NetAmount,
                ReceivedAmount = _db.Payments.Where(p => p.InvoiceId == i.Id).Sum(p => (decimal?)p.NetPaid) ?? 0m,
                GrnCount = _db.GoodsReceipts.Count(g => g.InvoiceId == i.Id),
                LatestGrn = _db.GoodsReceipts
                    .Where(g => g.InvoiceId == i.Id && g.GrnStatus == GrnStatus.GrnApproved)
                    .OrderByDescending(g => g.GrnApprovedAt).ThenByDescending(g => g.GrnDate).ThenByDescending(g => g.Seq)
                    .Select(g => new { g.GrnNumber, g.GrnApprovedAt, g.GrnDate })
                    .FirstOrDefault(),
                IssueReported = _db.GoodsReceipts
                    .Where(g => g.InvoiceId == i.Id && g.IssueReported != null && g.IssueReported != "")
                    .OrderByDescending(g => g.GrnApprovedAt).ThenByDescending(g => g.GrnDate).ThenByDescending(g => g.Seq)
                    .Select(g => g.IssueReported)
                    .FirstOrDefault(),
                LatestPaymentReference = _db.Payments
                    .Where(p => p.InvoiceId == i.Id)
                    .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Seq)
                    .Select(p => p.PaymentReference)
                    .FirstOrDefault(),
                // Net days: PO term first, fall back to the supplier term.
                PoNetDays = _db.PurchaseOrders
                    .Where(po => po.Id == i.PurchaseOrderId && po.PaymentTermId != null)
                    .Join(_db.PaymentTerms, po => po.PaymentTermId, t => (Guid?)t.Id, (po, t) => (int?)t.NetDays)
                    .FirstOrDefault(),
                SupplierNetDays = _db.Suppliers
                    .Where(s => s.Id == i.SupplierId && s.PaymentTermId != null)
                    .Join(_db.PaymentTerms, s => s.PaymentTermId, t => (Guid?)t.Id, (s, t) => (int?)t.NetDays)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var items = raw.Select(x =>
        {
            var netDays = x.PoNetDays ?? x.SupplierNetDays;
            DateTime? due = netDays.HasValue ? x.InvoiceDate.AddDays(netDays.Value) : null;
            return new PaymentSummaryRowDto(
                x.InvoiceNumber,
                x.InvoiceDate,
                x.NetAmount,
                x.LatestGrn?.GrnNumber,
                x.LatestGrn != null ? (x.LatestGrn.GrnApprovedAt ?? x.LatestGrn.GrnDate) : null,
                x.GrnCount,
                x.IssueReported,
                due,
                x.LatestPaymentReference,
                x.ReceivedAmount,
                x.NetAmount - x.ReceivedAmount);
        }).ToList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<PaymentSummaryRowDto>(items, page, pageSize, total, totalPages);
    }
}
