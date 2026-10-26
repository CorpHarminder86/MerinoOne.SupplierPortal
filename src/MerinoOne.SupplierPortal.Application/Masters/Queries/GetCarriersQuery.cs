using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

// R17 (2026-10-26) — Carrier list (the ASN Carrier dropdown source + the admin screen). Company-scoped and share-group aware
// through the Carrier global query filter.
public record GetCarriersQuery(bool? IsActive = null) : IRequest<List<CarrierDto>>;

public class GetCarriersQueryHandler : IRequestHandler<GetCarriersQuery, List<CarrierDto>>
{
    private readonly IAppDbContext _db;
    public GetCarriersQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<CarrierDto>> Handle(GetCarriersQuery request, CancellationToken ct)
    {
        var q = _db.Carriers.AsQueryable();
        if (request.IsActive.HasValue)
            q = q.Where(c => c.IsActive == request.IsActive.Value);

        return await q.OrderBy(c => c.Code)
            .Select(c => new CarrierDto(c.Id, c.Seq, c.Code, c.Description, c.IsActive, c.CreatedOn))
            .ToListAsync(ct);
    }
}
