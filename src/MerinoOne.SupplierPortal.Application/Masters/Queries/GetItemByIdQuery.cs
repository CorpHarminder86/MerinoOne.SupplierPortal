using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

public record GetItemByIdQuery(Guid Id) : IRequest<ItemDto>;

public class GetItemByIdQueryHandler : IRequestHandler<GetItemByIdQuery, ItemDto>
{
    private readonly IAppDbContext _db;
    public GetItemByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<ItemDto> Handle(GetItemByIdQuery request, CancellationToken ct)
    {
        return await _db.Items.Where(x => x.Id == request.Id)
            .Select(i => new ItemDto(i.Id, i.Seq, i.Code, i.Description, i.HsnCode,
                i.ItemGroupId, i.ItemGroup!.Code, i.UnitId, i.Unit!.Code, i.IsActive, i.CreatedOn))
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Item", request.Id);
    }
}
