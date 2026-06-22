using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

public record UpdateSupplierLicenseCommand(Guid SupplierId, Guid LicenseId, UpdateSupplierLicenseRequest Body)
    : IRequest<SupplierLicenseDto>;

public class UpdateSupplierLicenseCommandValidator : AbstractValidator<UpdateSupplierLicenseCommand>
{
    public UpdateSupplierLicenseCommandValidator()
    {
        RuleFor(x => x.Body.LicenseNumber).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.LicenseType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.Remarks).MaximumLength(1000);
        RuleFor(x => x.Body)
            .Must(b => !(b.IssueDate.HasValue && b.ExpiryDate.HasValue) || b.ExpiryDate!.Value >= b.IssueDate!.Value)
            .WithMessage("ExpiryDate must be on or after IssueDate.")
            .WithName("expiryDate");
    }
}

public class UpdateSupplierLicenseCommandHandler : IRequestHandler<UpdateSupplierLicenseCommand, SupplierLicenseDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public UpdateSupplierLicenseCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<SupplierLicenseDto> Handle(UpdateSupplierLicenseCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        var entity = await _db.SupplierLicenses
            .FirstOrDefaultAsync(l => l.Id == request.LicenseId && l.SupplierId == supplier.Id, ct)
            ?? throw new NotFoundException("SupplierLicense", request.LicenseId);

        entity.LicenseNumber = request.Body.LicenseNumber.Trim();
        entity.LicenseType = request.Body.LicenseType.Trim();
        entity.Remarks = string.IsNullOrWhiteSpace(request.Body.Remarks) ? null : request.Body.Remarks.Trim();
        entity.IssueDate = request.Body.IssueDate;
        entity.ExpiryDate = request.Body.ExpiryDate;
        entity.UpdatedBy = _user.UserCode;
        entity.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new SupplierLicenseDto(
            entity.Id, entity.Seq, entity.SupplierId, entity.LicenseNumber, entity.LicenseType,
            entity.Remarks, entity.IssueDate, entity.ExpiryDate, entity.ErpCode, entity.CreatedOn);
    }
}
