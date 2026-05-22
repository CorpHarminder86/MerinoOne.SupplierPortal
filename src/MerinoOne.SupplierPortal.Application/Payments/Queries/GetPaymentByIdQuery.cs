using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Payments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Payments.Queries;

public record GetPaymentByIdQuery(Guid Id) : IRequest<PaymentDetailDto>;

public class GetPaymentByIdQueryHandler : IRequestHandler<GetPaymentByIdQuery, PaymentDetailDto>
{
    private readonly IAppDbContext _db;
    public GetPaymentByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PaymentDetailDto> Handle(GetPaymentByIdQuery request, CancellationToken ct)
    {
        var row = await (from p in _db.Payments
                         join inv in _db.Invoices on p.InvoiceId equals inv.Id
                         join s in _db.Suppliers on p.SupplierId equals s.Id
                         where p.Id == request.Id
                         select new
                         {
                             p,
                             InvoiceNumber = inv.InvoiceNumber,
                             SupplierName = s.LegalName
                         }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Payment", request.Id);

        return new PaymentDetailDto(
            row.p.Id,
            row.p.Seq,
            row.p.PaymentReference,
            row.p.InvoiceId,
            row.InvoiceNumber,
            row.p.SupplierId,
            row.SupplierName,
            row.p.PaymentDate,
            row.p.PaymentAmount,
            row.p.NetPaid,
            row.p.PaymentMode,
            row.p.BankName,
            row.p.BankAccountRef,
            row.p.TdsDeducted,
            row.p.TdsSection,
            row.p.Remarks,
            row.p.RemittancePdfUrl,
            row.p.ErpSyncId,
            row.p.CreatedOn);
    }
}
