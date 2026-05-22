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
        var i = await _db.Items.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Item", request.Id);
        return new ItemDto(i.Id, i.Seq, i.Code, i.Description, i.Uom, i.HsnCode, i.IsActive, i.CreatedOn);
    }
}
