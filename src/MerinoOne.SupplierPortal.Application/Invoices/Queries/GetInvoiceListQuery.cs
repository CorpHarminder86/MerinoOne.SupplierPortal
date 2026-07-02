using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices.Queries;

/// <summary>
/// R4 (2026-06-22) — Module 4. Paged invoice list. Multi-PO-aware (Q1b): the header PO may be null, so PO display
/// is derived from the invoice lines' distinct POs (PoSummary = comma-joined PO numbers, PoCount = covered count).
/// The <c>?? Guid.Empty</c> shim is removed; <c>PurchaseOrderId</c> filter matches the header PO OR any line's PO.
/// </summary>
public record GetInvoiceListQuery(
    int Page = 1,
    int PageSize = 50,
    string? Status = null,
    Guid? SupplierId = null,
    Guid? PurchaseOrderId = null,
    string? Search = null) : IRequest<PagedResult<InvoiceListItemDto>>;

public class GetInvoiceListQueryHandler : IRequestHandler<GetInvoiceListQuery, PagedResult<InvoiceListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetInvoiceListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<InvoiceListItemDto>> Handle(GetInvoiceListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var q = from inv in _db.Invoices
                join s in _db.Suppliers on inv.SupplierId equals s.Id
                select new { inv, s };

        if (!string.IsNullOrWhiteSpace(request.Status))
            q = q.Where(x => x.inv.InvoiceStatus.ToString() == request.Status);
        if (request.SupplierId.HasValue)
            q = q.Where(x => x.inv.SupplierId == request.SupplierId.Value);
        if (request.PurchaseOrderId.HasValue)
        {
            var poId = request.PurchaseOrderId.Value;
            q = q.Where(x => x.inv.PurchaseOrderId == poId
                             || _db.InvoiceLines.Any(il => il.InvoiceId == x.inv.Id
                                  && _db.PurchaseOrderLines.Any(pol => pol.Id == il.PurchaseOrderLineId && pol.PurchaseOrderId == poId)));
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.inv.InvoiceNumber.Contains(t));
        }

        var total = await q.CountAsync(ct);

        var page1 = await q
            .OrderByDescending(x => x.inv.InvoiceDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.inv.Id, x.inv.Seq, x.inv.InvoiceNumber, x.inv.PurchaseOrderId,
                x.inv.SupplierId, SupplierName = x.s.LegalName,
                x.inv.InvoiceDate, x.inv.InvoiceAmount, x.inv.TaxAmount, x.inv.NetAmount,
                x.inv.CurrencyCode, MatchingType = x.inv.MatchingType.ToString(),
                Status = x.inv.InvoiceStatus.ToString(), x.inv.CreatedOn,
                Origin = x.inv.InvoiceOrigin.ToString(),
            })
            .ToListAsync(ct);

        var invIds = page1.Select(x => x.Id).ToList();

        // Covered PO numbers per invoice from the lines (one round-trip).
        var linePos = await (from il in _db.InvoiceLines
                             join pol in _db.PurchaseOrderLines on il.PurchaseOrderLineId equals pol.Id
                             join po in _db.PurchaseOrders on pol.PurchaseOrderId equals po.Id
                             where invIds.Contains(il.InvoiceId)
                             select new { il.InvoiceId, po.PoNumber })
                            .Distinct().ToListAsync(ct);
        var poNumbersByInvoice = linePos
            .GroupBy(x => x.InvoiceId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PoNumber).Distinct().OrderBy(n => n).ToList());

        var headerPoIds = page1.Where(x => x.PurchaseOrderId.HasValue).Select(x => x.PurchaseOrderId!.Value).Distinct().ToList();
        var headerPos = await _db.PurchaseOrders.Where(p => headerPoIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PoNumber }).ToDictionaryAsync(p => p.Id, ct);

        var items = page1.Select(x =>
        {
            var nums = poNumbersByInvoice.TryGetValue(x.Id, out var list) ? new List<string>(list) : new List<string>();
            string? headerPoNumber = x.PurchaseOrderId.HasValue && headerPos.TryGetValue(x.PurchaseOrderId.Value, out var hp) ? hp.PoNumber : null;
            if (headerPoNumber is not null && !nums.Contains(headerPoNumber)) nums.Insert(0, headerPoNumber);
            var summary = nums.Count == 0 ? "—" : string.Join(", ", nums);
            return new InvoiceListItemDto(
                x.Id, x.Seq, x.InvoiceNumber, x.PurchaseOrderId, headerPoNumber, summary, nums.Count,
                x.SupplierId, x.SupplierName,
                x.InvoiceDate, x.InvoiceAmount, x.TaxAmount, x.NetAmount,
                x.CurrencyCode, x.MatchingType, x.Status, x.CreatedOn, x.Origin);
        }).ToList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<InvoiceListItemDto>(items, page, pageSize, total, totalPages);
    }
}
