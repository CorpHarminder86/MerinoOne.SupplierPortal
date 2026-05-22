using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

public record DeactivatePaymentTermCommand(Guid Id) : IRequest<Unit>;

public class DeactivatePaymentTermCommandHandler : IRequestHandler<DeactivatePaymentTermCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeactivatePaymentTermCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(DeactivatePaymentTermCommand request, CancellationToken ct)
    {
        var p = await _db.PaymentTerms.FirstOrDefaultAsync(x => x.Id == request.Id, ct)
                ?? throw new NotFoundException("PaymentTerm", request.Id);

        p.IsActive = false;
        p.UpdatedBy = _user.UserCode;
        p.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
