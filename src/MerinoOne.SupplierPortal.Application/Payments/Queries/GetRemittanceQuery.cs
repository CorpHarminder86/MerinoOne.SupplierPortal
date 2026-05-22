using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Payments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Payments.Queries;

public record GetRemittanceQuery(Guid PaymentId) : IRequest<RemittanceDto>;

public class GetRemittanceQueryHandler : IRequestHandler<GetRemittanceQuery, RemittanceDto>
{
    private readonly IAppDbContext _db;
    public GetRemittanceQueryHandler(IAppDbContext db) => _db = db;

    public async Task<RemittanceDto> Handle(GetRemittanceQuery request, CancellationToken ct)
    {
        var p = await _db.Payments
            .Where(x => x.Id == request.PaymentId)
            .Select(x => new
            {
                x.Id,
                x.RemittancePdfUrl,
                x.PaymentReference,
                x.NetPaid,
                x.PaymentDate
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Payment", request.PaymentId);

        return new RemittanceDto(p.Id, p.RemittancePdfUrl, p.PaymentReference, p.NetPaid, p.PaymentDate);
    }
}
