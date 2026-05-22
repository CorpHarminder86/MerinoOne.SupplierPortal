using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record DeactivateItemCommand(Guid Id) : IRequest<Unit>;

public class DeactivateItemCommandHandler : IRequestHandler<DeactivateItemCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeactivateItemCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(DeactivateItemCommand request, CancellationToken ct)
    {
        var i = await _db.Items.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("Item", request.Id);

        i.IsActive = false;
        i.UpdatedBy = _user.UserCode;
        i.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
