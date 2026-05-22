using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record DeactivateDeliveryTermCommand(Guid Id) : IRequest<Unit>;

public class DeactivateDeliveryTermCommandHandler : IRequestHandler<DeactivateDeliveryTermCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeactivateDeliveryTermCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(DeactivateDeliveryTermCommand request, CancellationToken ct)
    {
        var d = await _db.DeliveryTerms.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("DeliveryTerm", request.Id);

        d.IsActive = false;
        d.UpdatedBy = _user.UserCode;
        d.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
