using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices.Queries;

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
                join po in _db.PurchaseOrders on inv.PurchaseOrderId equals po.Id
                join s in _db.Suppliers on inv.SupplierId equals s.Id
                select new { inv, po, s };

        if (!string.IsNullOrWhiteSpace(request.Status))
            q = q.Where(x => x.inv.InvoiceStatus.ToString() == request.Status);
        if (request.SupplierId.HasValue)
            q = q.Where(x => x.inv.SupplierId == request.SupplierId.Value);
        if (request.PurchaseOrderId.HasValue)
            q = q.Where(x => x.inv.PurchaseOrderId == request.PurchaseOrderId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.inv.InvoiceNumber.Contains(t));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(x => x.inv.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new InvoiceListItemDto(
                x.inv.Id,
                x.inv.Seq,
                x.inv.InvoiceNumber,
                x.inv.PurchaseOrderId,
                x.po.PoNumber,
                x.inv.SupplierId,
                x.s.LegalName,
                x.inv.InvoiceDate,
                x.inv.InvoiceAmount,
                x.inv.TaxAmount,
                x.inv.NetAmount,
                x.inv.CurrencyCode,
                x.inv.MatchingType.ToString(),
                x.inv.InvoiceStatus.ToString(),
                x.inv.CreatedOn))
            .ToListAsync(ct);

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<InvoiceListItemDto>(items, page, pageSize, total, totalPages);
    }
}
