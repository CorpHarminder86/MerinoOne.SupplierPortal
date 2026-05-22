using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices.Queries;

public record GetCreditDebitNoteListQuery(
    int Page = 1,
    int PageSize = 50,
    string? Status = null,
    Guid? InvoiceId = null,
    string? NoteType = null,
    string? Search = null) : IRequest<PagedResult<CreditDebitNoteListItemDto>>;

public class GetCreditDebitNoteListQueryHandler : IRequestHandler<GetCreditDebitNoteListQuery, PagedResult<CreditDebitNoteListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetCreditDebitNoteListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<CreditDebitNoteListItemDto>> Handle(GetCreditDebitNoteListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var q = from n in _db.CreditDebitNotes
                join inv in _db.Invoices on n.InvoiceId equals inv.Id
                select new { n, inv };

        if (!string.IsNullOrWhiteSpace(request.Status))
            q = q.Where(x => x.n.NoteStatus.ToString() == request.Status);
        if (!string.IsNullOrWhiteSpace(request.NoteType))
            q = q.Where(x => x.n.NoteType.ToString() == request.NoteType);
        if (request.InvoiceId.HasValue)
            q = q.Where(x => x.n.InvoiceId == request.InvoiceId.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.n.NoteNumber.Contains(t) || x.inv.InvoiceNumber.Contains(t));
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(x => x.n.CreatedOn)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new CreditDebitNoteListItemDto(
                x.n.Id,
                x.n.Seq,
                x.n.NoteNumber,
                x.n.NoteType.ToString(),
                x.n.InvoiceId,
                x.inv.InvoiceNumber,
                x.n.Amount,
                x.n.NoteStatus.ToString(),
                x.n.CreatedOn))
            .ToListAsync(ct);

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<CreditDebitNoteListItemDto>(items, page, pageSize, total, totalPages);
    }
}
