using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

/// <summary>
/// Admin sets a supplier's PO-response mode (Manual / Auto). Editable post-approval (the field is NOT frozen at
/// approval — §3.1(g)). For <c>Auto</c> the PO release path auto-acknowledges + auto-confirms + posts acceptance
/// (feature-flagged hook — see <c>AutoPoReleaseHook</c>).
/// </summary>
public record SetSupplierPoResponseModeCommand(Guid SupplierId, SetPoResponseModeRequest Body) : IRequest<Unit>;

public class SetSupplierPoResponseModeCommandValidator : AbstractValidator<SetSupplierPoResponseModeCommand>
{
    public SetSupplierPoResponseModeCommandValidator()
    {
        RuleFor(x => x.Body.PoResponseMode)
            .NotEmpty()
            .Must(v => Enum.TryParse<PoResponseMode>(v, ignoreCase: true, out _))
            .WithMessage($"PoResponseMode must be one of: {string.Join(", ", Enum.GetNames<PoResponseMode>())}.");
    }
}

public class SetSupplierPoResponseModeCommandHandler : IRequestHandler<SetSupplierPoResponseModeCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public SetSupplierPoResponseModeCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(SetSupplierPoResponseModeCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        supplier.PoResponseMode = Enum.Parse<PoResponseMode>(request.Body.PoResponseMode, ignoreCase: true);
        supplier.UpdatedBy = _user.UserCode;
        supplier.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
