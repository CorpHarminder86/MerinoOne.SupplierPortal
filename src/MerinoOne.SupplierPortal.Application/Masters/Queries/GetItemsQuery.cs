using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

public record GetItemsQuery(bool? IsActive = null, string? Search = null) : IRequest<List<ItemDto>>;

public class GetItemsQueryHandler : IRequestHandler<GetItemsQuery, List<ItemDto>>
{
    private readonly IAppDbContext _db;
    public GetItemsQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<ItemDto>> Handle(GetItemsQuery request, CancellationToken ct)
    {
        var q = _db.Items.AsQueryable();
        if (request.IsActive.HasValue)
            q = q.Where(i => i.IsActive == request.IsActive.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(i => i.Code.Contains(t) || i.Description.Contains(t));
        }

        return await q.OrderBy(i => i.Code)
            .Select(i => new ItemDto(i.Id, i.Seq, i.Code, i.Description, i.HsnCode,
                i.ItemGroupId, i.ItemGroup!.Code, i.UnitId, i.Unit!.Code, i.IsActive, i.CreatedOn,
                i.IsSerialized, i.IsLotControlled, i.OverShipTolerancePct))
            .ToListAsync(ct);
    }
}
