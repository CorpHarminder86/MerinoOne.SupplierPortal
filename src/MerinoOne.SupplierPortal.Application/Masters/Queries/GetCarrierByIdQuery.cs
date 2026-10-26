using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

// R17 (2026-10-26) — single Carrier by id (company-scoped through the global query filter, so another company's id is a 404).
public record GetCarrierByIdQuery(Guid Id) : IRequest<CarrierDto>;

public class GetCarrierByIdQueryHandler : IRequestHandler<GetCarrierByIdQuery, CarrierDto>
{
    private readonly IAppDbContext _db;
    public GetCarrierByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<CarrierDto> Handle(GetCarrierByIdQuery request, CancellationToken ct)
    {
        var c = await _db.Carriers.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Carrier", request.Id);
        return new CarrierDto(c.Id, c.Seq, c.Code, c.Description, c.IsActive, c.CreatedOn);
    }
}
