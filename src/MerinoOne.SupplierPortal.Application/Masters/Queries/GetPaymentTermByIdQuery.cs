using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

public record GetPaymentTermByIdQuery(Guid Id) : IRequest<PaymentTermDto>;

public class GetPaymentTermByIdQueryHandler : IRequestHandler<GetPaymentTermByIdQuery, PaymentTermDto>
{
    private readonly IAppDbContext _db;
    public GetPaymentTermByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PaymentTermDto> Handle(GetPaymentTermByIdQuery request, CancellationToken ct)
    {
        var p = await _db.PaymentTerms.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("PaymentTerm", request.Id);
        return new PaymentTermDto(p.Id, p.Seq, p.Code, p.Description, p.NetDays, p.IsActive, p.CreatedOn);
    }
}
