using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Queries;

/// <summary>
/// Lists the current tenant's Infor endpoint config + session telemetry. The always-on tenant filter on
/// InforEndpointMap scopes the result to the caller's tenant automatically (no IgnoreQueryFilters).
/// </summary>
public record GetInforEndpointsQuery(string? Direction = null) : IRequest<List<InforEndpointDto>>;

public class GetInforEndpointsQueryHandler : IRequestHandler<GetInforEndpointsQuery, List<InforEndpointDto>>
{
    private readonly IAppDbContext _db;
    public GetInforEndpointsQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<InforEndpointDto>> Handle(GetInforEndpointsQuery request, CancellationToken ct)
    {
        var q = _db.InforEndpointMaps.AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Direction))
            q = q.Where(m => m.Direction.ToString() == request.Direction);

        return await q
            .OrderBy(m => m.EntityName).ThenBy(m => m.Direction)
            .Select(m => new InforEndpointDto(
                m.Id, m.Seq, m.EntityName, m.Direction.ToString(), m.InforEndpointUrl, m.BodName,
                m.IsEnabled, m.LastReceivedAt, m.LastStatus, m.LastIdempotencyKey, m.LastMessage,
                m.ReceivedCount, m.CreatedOn))
            .ToListAsync(ct);
    }
}
