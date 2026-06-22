using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

public record GetTaxByIdQuery(Guid Id) : IRequest<TaxDto>;

public class GetTaxByIdQueryHandler : IRequestHandler<GetTaxByIdQuery, TaxDto>
{
    private readonly IAppDbContext _db;
    public GetTaxByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<TaxDto> Handle(GetTaxByIdQuery request, CancellationToken ct)
    {
        var t = await _db.Taxes.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Tax", request.Id);
        return new TaxDto(t.Id, t.Seq, t.Code, t.Description, t.TaxRate, t.IsActive, t.CreatedOn);
    }
}
