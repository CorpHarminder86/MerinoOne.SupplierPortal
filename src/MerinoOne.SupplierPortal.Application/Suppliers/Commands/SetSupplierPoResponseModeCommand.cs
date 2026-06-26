using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

/// <summary>
/// R4 (2026-06-26) — D1: admin sets a supplier's PO confirmation mode (AutoAccept / AcknowledgeToShip /
/// AcceptToShip) plus the action toggles AllowNegotiate / AllowReject. Editable post-approval (the field is NOT
/// frozen at approval — §3.1(g)). For <c>AutoAccept</c> the PO release path auto-stamps Accepted + posts the
/// acceptance (feature-flagged hook — see <c>ApplyAutoPoReleaseCommand</c>).
/// </summary>
public record SetSupplierPoResponseModeCommand(Guid SupplierId, SetPoResponseModeRequest Body) : IRequest<Unit>;

public class SetSupplierPoResponseModeCommandValidator : AbstractValidator<SetSupplierPoResponseModeCommand>
{
    public SetSupplierPoResponseModeCommandValidator()
    {
        RuleFor(x => x.Body.PoResponseMode)
            .NotEmpty()
            .Must(v => Enum.TryParse<PoConfirmationMode>(v, ignoreCase: true, out _))
            .WithMessage($"PoResponseMode must be one of: {string.Join(", ", Enum.GetNames<PoConfirmationMode>())}.");
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

        supplier.PoConfirmationMode = Enum.Parse<PoConfirmationMode>(request.Body.PoResponseMode, ignoreCase: true);
        supplier.AllowNegotiate = request.Body.AllowNegotiate;
        supplier.AllowReject = request.Body.AllowReject;
        supplier.UpdatedBy = _user.UserCode;
        supplier.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
