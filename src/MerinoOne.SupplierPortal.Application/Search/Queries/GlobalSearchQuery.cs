using Dapper;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Search;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Search.Queries;

/// <summary>
/// Cross-module global search (TSD §11 cross-cutting).
/// Returns a uniform <see cref="SearchResultDto"/> shape so the UI can render one list.
/// <para>Two execution paths:</para>
/// <list type="bullet">
///   <item>Admin / Manager: a Dapper UNION ALL across 7 source tables. Seccode is not applied —
///         these users see everything.</item>
///   <item>Supplier (non-privileged) users: per-module EF queries against <see cref="IAppDbContext"/>
///         so the global query filter scopes by <c>seccodeId</c> via <c>SecRight</c>.</item>
/// </list>
/// </summary>
public record GlobalSearchQuery(
    string Q,
    string? Module = null,
    DateTime? From = null,
    DateTime? To = null,
    int Limit = 50) : IRequest<List<SearchResultDto>>;

public class GlobalSearchQueryHandler : IRequestHandler<GlobalSearchQuery, List<SearchResultDto>>
{
    private readonly ISqlConnectionFactory _sql;
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public GlobalSearchQueryHandler(ISqlConnectionFactory sql, IAppDbContext db, ICurrentUser user)
    {
        _sql = sql;
        _db = db;
        _user = user;
    }

    public async Task<List<SearchResultDto>> Handle(GlobalSearchQuery request, CancellationToken ct)
    {
        var q = (request.Q ?? string.Empty).Trim();
        if (q.Length == 0) return new List<SearchResultDto>();

        var limit = request.Limit <= 0 ? 50 : Math.Min(request.Limit, 500);
        var like = $"%{q}%";

        // Privileged users → one UNION ALL with Dapper. Non-privileged supplier users → EF per module
        // so the seccode global filter applies.
        return _user.IsAdmin || _user.IsManager
            ? await SearchViaDapperAsync(like, request.Module, request.From, request.To, limit, ct)
            : await SearchViaEfAsync(q, request.Module, request.From, request.To, limit, ct);
    }

    // --- privileged path ---------------------------------------------------------

    private async Task<List<SearchResultDto>> SearchViaDapperAsync(
        string like, string? module, DateTime? from, DateTime? to, int limit, CancellationToken ct)
    {
        // Build one CTE-style query so module / from / to can filter the outer set.
        // Each leg projects: module, id, code, title, subtitle, status, when.
        const string sql = @"
WITH hits AS (
    SELECT 'Supplier' AS module, supplierId AS id, supplierCode AS code,
           legalName AS title, ISNULL(tradeName, N'') AS subtitle,
           CAST(registrationStatus AS NVARCHAR(50)) AS status, createdOn AS [when]
      FROM [supplier].[Supplier]
     WHERE isDeleted = 0
       AND tenantId = @tenantId
       AND (supplierCode LIKE @q OR legalName LIKE @q OR ISNULL(tradeName, N'') LIKE @q)
    UNION ALL
    SELECT 'PurchaseOrder', purchaseOrderId, poNumber, poNumber,
           CONCAT(N'Supplier ', CAST(supplierId AS NVARCHAR(40))),
           CAST(poStatus AS NVARCHAR(50)), poDate
      FROM [proc].[PurchaseOrder]
     WHERE isDeleted = 0
       AND tenantId = @tenantId
       AND (poNumber LIKE @q OR ISNULL(notes, N'') LIKE @q)
    UNION ALL
    SELECT 'Invoice', invoiceId, invoiceNumber, invoiceNumber,
           CONCAT(N'Supplier ', CAST(supplierId AS NVARCHAR(40))),
           CAST(invoiceStatus AS NVARCHAR(50)), invoiceDate
      FROM [proc].[Invoice]
     WHERE isDeleted = 0
       AND tenantId = @tenantId
       AND (invoiceNumber LIKE @q OR ISNULL(eInvoiceIrn, N'') LIKE @q)
    UNION ALL
    SELECT 'Asn', asnId, asnNumber, asnNumber,
           CONCAT(N'Supplier ', CAST(supplierId AS NVARCHAR(40))),
           CAST(asnStatus AS NVARCHAR(50)), expectedDeliveryDate
      FROM [proc].[Asn]
     WHERE isDeleted = 0
       AND tenantId = @tenantId
       AND (asnNumber LIKE @q OR ISNULL(trackingNumber, N'') LIKE @q)
    UNION ALL
    SELECT 'GoodsReceipt', goodsReceiptId, grnNumber, grnNumber,
           N'',
           CAST(CASE WHEN rejectedQty > 0 THEN N'WithRejections' ELSE N'Received' END AS NVARCHAR(50)),
           grnDate
      FROM [proc].[GoodsReceipt]
     WHERE isDeleted = 0
       AND tenantId = @tenantId
       AND grnNumber LIKE @q
    UNION ALL
    SELECT 'Payment', paymentId, paymentReference, paymentReference,
           CONCAT(N'Supplier ', CAST(supplierId AS NVARCHAR(40))),
           CAST(CASE WHEN netPaid > 0 THEN N'Paid' ELSE N'Pending' END AS NVARCHAR(50)),
           paymentDate
      FROM [proc].[Payment]
     WHERE isDeleted = 0
       AND tenantId = @tenantId
       AND paymentReference LIKE @q
    UNION ALL
    SELECT 'CommunicationMessage', communicationMessageId, CAST(threadId AS NVARCHAR(40)),
           LEFT(messageBody, 120), N'',
           CAST(CASE WHEN isRead = 1 THEN N'Read' ELSE N'Unread' END AS NVARCHAR(50)),
           sentAt
      FROM [comm].[CommunicationMessage]
     WHERE isDeleted = 0
       AND tenantId = @tenantId
       AND messageBody LIKE @q
)
SELECT TOP (@limit) module AS Module, id AS Id, code AS Code, title AS Title,
       subtitle AS Subtitle, status AS Status, [when] AS [When]
  FROM hits
 WHERE (@module IS NULL OR module = @module)
   AND (@from IS NULL OR [when] >= @from)
   AND (@to IS NULL OR [when] <= @to)
 ORDER BY [when] DESC;";

        await using var cn = await _sql.OpenAsync(ct);
        var rows = await cn.QueryAsync<SearchResultDto>(new CommandDefinition(
            sql,
            // SECURITY: tenant-scope the privileged Dapper search (raw SQL bypasses the EF global filter).
            new { q = like, module, from, to, limit, tenantId = _user.TenantId },
            cancellationToken: ct));
        return rows.AsList();
    }

    // --- supplier / non-privileged path ------------------------------------------

    private async Task<List<SearchResultDto>> SearchViaEfAsync(
        string q, string? module, DateTime? from, DateTime? to, int limit, CancellationToken ct)
    {
        // EF projection — relies on AppDbContext's global seccode filter to scope rows.
        // Smaller per-module limit since we'll merge & sort in memory.
        var perModule = Math.Max(10, limit);
        var hits = new List<SearchResultDto>(capacity: perModule * 4);

        if (Include(module, "Supplier"))
        {
            var src = await _db.Suppliers
                .Where(s => s.SupplierCode.Contains(q) || s.LegalName.Contains(q) || (s.TradeName ?? "").Contains(q))
                .OrderByDescending(s => s.CreatedOn).Take(perModule)
                .Select(s => new SearchResultDto(
                    "Supplier", s.Id, s.SupplierCode, s.LegalName,
                    s.TradeName ?? string.Empty,
                    s.RegistrationStatus.ToString(), s.CreatedOn))
                .ToListAsync(ct);
            hits.AddRange(src);
        }

        if (Include(module, "PurchaseOrder"))
        {
            var src = await _db.PurchaseOrders
                .Where(p => p.PoNumber.Contains(q) || (p.Notes ?? "").Contains(q))
                .OrderByDescending(p => p.PoDate).Take(perModule)
                .Select(p => new SearchResultDto(
                    "PurchaseOrder", p.Id, p.PoNumber, p.PoNumber,
                    "Supplier " + p.SupplierId,
                    p.PoStatus.ToString(), p.PoDate))
                .ToListAsync(ct);
            hits.AddRange(src);
        }

        if (Include(module, "Invoice"))
        {
            var src = await _db.Invoices
                .Where(i => i.InvoiceNumber.Contains(q) || (i.EInvoiceIrn ?? "").Contains(q))
                .OrderByDescending(i => i.InvoiceDate).Take(perModule)
                .Select(i => new SearchResultDto(
                    "Invoice", i.Id, i.InvoiceNumber, i.InvoiceNumber,
                    "Supplier " + i.SupplierId,
                    i.InvoiceStatus.ToString(), i.InvoiceDate))
                .ToListAsync(ct);
            hits.AddRange(src);
        }

        if (Include(module, "Asn"))
        {
            var src = await _db.Asns
                .Where(a => a.AsnNumber.Contains(q) || (a.TrackingNumber ?? "").Contains(q))
                .OrderByDescending(a => a.ExpectedDeliveryDate).Take(perModule)
                .Select(a => new SearchResultDto(
                    "Asn", a.Id, a.AsnNumber, a.AsnNumber,
                    "Supplier " + a.SupplierId,
                    a.AsnStatus.ToString(), a.ExpectedDeliveryDate))
                .ToListAsync(ct);
            hits.AddRange(src);
        }

        if (Include(module, "GoodsReceipt"))
        {
            var src = await _db.GoodsReceipts
                .Where(g => g.GrnNumber.Contains(q))
                .OrderByDescending(g => g.GrnDate).Take(perModule)
                .Select(g => new SearchResultDto(
                    "GoodsReceipt", g.Id, g.GrnNumber, g.GrnNumber,
                    string.Empty,
                    g.RejectedQty > 0 ? "WithRejections" : "Received",
                    g.GrnDate))
                .ToListAsync(ct);
            hits.AddRange(src);
        }

        if (Include(module, "Payment"))
        {
            var src = await _db.Payments
                .Where(p => p.PaymentReference.Contains(q))
                .OrderByDescending(p => p.PaymentDate).Take(perModule)
                .Select(p => new SearchResultDto(
                    "Payment", p.Id, p.PaymentReference, p.PaymentReference,
                    "Supplier " + p.SupplierId,
                    p.NetPaid > 0 ? "Paid" : "Pending",
                    p.PaymentDate))
                .ToListAsync(ct);
            hits.AddRange(src);
        }

        if (Include(module, "CommunicationMessage"))
        {
            var src = await _db.CommunicationMessages
                .Where(c => c.MessageBody.Contains(q))
                .OrderByDescending(c => c.SentAt).Take(perModule)
                .Select(c => new SearchResultDto(
                    "CommunicationMessage", c.Id, c.ThreadId.ToString(),
                    c.MessageBody.Length > 120 ? c.MessageBody.Substring(0, 120) : c.MessageBody,
                    string.Empty,
                    c.IsRead ? "Read" : "Unread",
                    c.SentAt))
                .ToListAsync(ct);
            hits.AddRange(src);
        }

        IEnumerable<SearchResultDto> filtered = hits;
        if (from.HasValue) filtered = filtered.Where(h => h.When >= from.Value);
        if (to.HasValue) filtered = filtered.Where(h => h.When <= to.Value);

        return filtered
            .OrderByDescending(h => h.When)
            .Take(limit)
            .ToList();
    }

    private static bool Include(string? requested, string actual)
        => string.IsNullOrEmpty(requested) || string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase);
}
