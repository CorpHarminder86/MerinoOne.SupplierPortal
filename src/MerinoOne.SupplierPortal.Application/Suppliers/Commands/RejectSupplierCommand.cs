using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

public record RejectSupplierCommand(Guid SupplierId, RejectSupplierRequest Body) : IRequest<Unit>;

public class RejectSupplierCommandValidator : AbstractValidator<RejectSupplierCommand>
{
    public RejectSupplierCommandValidator()
    {
        RuleFor(x => x.Body.Reason).NotEmpty().MaximumLength(1000);
    }
}

public class RejectSupplierCommandHandler : IRequestHandler<RejectSupplierCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public RejectSupplierCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(RejectSupplierCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        supplier.RegistrationStatus = RegistrationStatus.Rejected;
        supplier.IsActiveSupplier = false;
        supplier.RejectionReason = request.Body.Reason;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
