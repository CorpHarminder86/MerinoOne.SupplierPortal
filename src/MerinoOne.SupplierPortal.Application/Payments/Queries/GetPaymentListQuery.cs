using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Payments;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Payments.Queries;

public record GetPaymentListQuery(
    int Page = 1,
    int PageSize = 50,
    Guid? SupplierId = null,
    Guid? InvoiceId = null,
    string? Search = null) : IRequest<PagedResult<PaymentListItemDto>>;

public class GetPaymentListQueryHandler : IRequestHandler<GetPaymentListQuery, PagedResult<PaymentListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetPaymentListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<PaymentListItemDto>> Handle(GetPaymentListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var q = from p in _db.Payments
                join inv in _db.Invoices on p.InvoiceId equals inv.Id
                join s in _db.Suppliers on p.SupplierId equals s.Id
                select new { p, inv, s };

        if (request.SupplierId.HasValue)
            q = q.Where(x => x.p.SupplierId == request.SupplierId.Value);
        if (request.InvoiceId.HasValue)
            q = q.Where(x => x.p.InvoiceId == request.InvoiceId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.p.PaymentReference.Contains(t) || x.inv.InvoiceNumber.Contains(t));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(x => x.p.PaymentDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new PaymentListItemDto(
                x.p.Id,
                x.p.Seq,
                x.p.PaymentReference,
                x.p.InvoiceId,
                x.inv.InvoiceNumber,
                x.p.SupplierId,
                x.s.LegalName,
                x.p.PaymentDate,
                x.p.PaymentAmount,
                x.p.NetPaid,
                x.p.PaymentMode,
                x.p.BankName))
            .ToListAsync(ct);

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<PaymentListItemDto>(items, page, pageSize, total, totalPages);
    }
}
