using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Queries;

public record GetDeliveryTermByIdQuery(Guid Id) : IRequest<MasterItemDto>;

public class GetDeliveryTermByIdQueryHandler : IRequestHandler<GetDeliveryTermByIdQuery, MasterItemDto>
{
    private readonly IAppDbContext _db;
    public GetDeliveryTermByIdQueryHandler(IAppDbContext db) => _db = db;

    public async Task<MasterItemDto> Handle(GetDeliveryTermByIdQuery request, CancellationToken ct)
    {
        var d = await _db.DeliveryTerms.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("DeliveryTerm", request.Id);
        return new MasterItemDto(d.Id, d.Seq, d.Code, d.Description, d.IsActive, d.CreatedOn);
    }
}
