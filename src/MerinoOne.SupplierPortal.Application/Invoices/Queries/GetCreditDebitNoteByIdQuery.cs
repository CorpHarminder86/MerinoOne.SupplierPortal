using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices.Queries;

public record GetCreditDebitNoteByIdQuery(Guid Id) : IRequest<CreditDebitNoteDetailDto>;

public class GetCreditDebitNoteByIdQueryHandler : IRequestHandler<GetCreditDebitNoteByIdQuery, CreditDebitNoteDetailDto>
{
    private readonly IAppDbContext _db;
    public GetCreditDebitNoteByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<CreditDebitNoteDetailDto> Handle(GetCreditDebitNoteByIdQuery request, CancellationToken ct)
    {
        var row = await (from n in _db.CreditDebitNotes
                         join inv in _db.Invoices on n.InvoiceId equals inv.Id
                         join s in _db.Suppliers on inv.SupplierId equals s.Id
                         where n.Id == request.Id
                         select new
                         {
                             n,
                             InvoiceNumber = inv.InvoiceNumber,
                             SupplierId = inv.SupplierId,
                             SupplierName = s.LegalName
                         }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("CreditDebitNote", request.Id);

        return new CreditDebitNoteDetailDto(
            row.n.Id,
            row.n.Seq,
            row.n.NoteNumber,
            row.n.NoteType.ToString(),
            row.n.InvoiceId,
            row.InvoiceNumber,
            row.SupplierId,
            row.SupplierName,
            row.n.Amount,
            row.n.Reason,
            row.n.NoteStatus.ToString(),
            row.n.CreatedOn);
    }
}
