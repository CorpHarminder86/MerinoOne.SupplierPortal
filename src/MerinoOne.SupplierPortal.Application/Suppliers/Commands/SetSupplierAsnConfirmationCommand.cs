using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

/// <summary>
/// R14 (2026-08-05) — admin sets a supplier's <b>ASN Confirmation Required</b> flag. Editable post-approval, like
/// the PO confirmation mode it sits beside (see <see cref="SetSupplierPoResponseModeCommand"/>), and orthogonal to
/// it: that one decides whether a PO may be shipped at all, this one decides whether the buyer confirms the
/// shipment before it dispatches.
///
/// <para>Flipping the flag is safe for in-flight ASNs. An ASN already in <c>Approved</c> stays postable in either
/// mode (<c>AsnLifecycle.AssertCanPost</c>), so switching a supplier to No never strands a confirmed shipment; and
/// switching to Yes simply means any remaining Draft must now be sent for approval before it can be posted.</para>
/// </summary>
public record SetSupplierAsnConfirmationCommand(Guid SupplierId, SetAsnConfirmationRequest Body) : IRequest<Unit>;

public class SetSupplierAsnConfirmationCommandValidator : AbstractValidator<SetSupplierAsnConfirmationCommand>
{
    public SetSupplierAsnConfirmationCommandValidator()
    {
        RuleFor(x => x.SupplierId).NotEmpty();
    }
}

public class SetSupplierAsnConfirmationCommandHandler : IRequestHandler<SetSupplierAsnConfirmationCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public SetSupplierAsnConfirmationCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(SetSupplierAsnConfirmationCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        supplier.AsnConfirmationRequired = request.Body.AsnConfirmationRequired;
        supplier.UpdatedBy = _user.UserCode;
        supplier.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
