using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

public record GetDeliveryTermsQuery(bool? IsActive = null) : IRequest<List<MasterItemDto>>;

public class GetDeliveryTermsQueryHandler : IRequestHandler<GetDeliveryTermsQuery, List<MasterItemDto>>
{
    private readonly IAppDbContext _db;
    public GetDeliveryTermsQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<MasterItemDto>> Handle(GetDeliveryTermsQuery request, CancellationToken ct)
    {
        var q = _db.DeliveryTerms.AsQueryable();
        if (request.IsActive.HasValue)
            q = q.Where(d => d.IsActive == request.IsActive.Value);

        return await q.OrderBy(d => d.Code)
            .Select(d => new MasterItemDto(d.Id, d.Seq, d.Code, d.Description, d.IsActive, d.CreatedOn))
            .ToListAsync(ct);
    }
}
