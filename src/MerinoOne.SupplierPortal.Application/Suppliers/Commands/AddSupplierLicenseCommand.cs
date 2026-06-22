using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

/// <summary>
/// Adds a license / certification to a supplier (Module 1). <see cref="SupplierLicense"/> is a seccode-protected
/// <c>BaseAggregateRoot</c>; <c>Owner</c> is stamped to the supplier's G-seccode and the write is canWrite-gated.
/// Attachments are wired separately via <c>doc.DocumentUpload</c> (ownerEntityType='SupplierLicense').
/// </summary>
public record AddSupplierLicenseCommand(Guid SupplierId, AddSupplierLicenseRequest Body) : IRequest<SupplierLicenseDto>;

public class AddSupplierLicenseCommandValidator : AbstractValidator<AddSupplierLicenseCommand>
{
    public AddSupplierLicenseCommandValidator()
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

public class AddSupplierLicenseCommandHandler : IRequestHandler<AddSupplierLicenseCommand, SupplierLicenseDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public AddSupplierLicenseCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<SupplierLicenseDto> Handle(AddSupplierLicenseCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        var entity = new SupplierLicense
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            LicenseNumber = request.Body.LicenseNumber.Trim(),
            LicenseType = request.Body.LicenseType.Trim(),
            Remarks = string.IsNullOrWhiteSpace(request.Body.Remarks) ? null : request.Body.Remarks.Trim(),
            IssueDate = request.Body.IssueDate,
            ExpiryDate = request.Body.ExpiryDate,
            SeccodeId = supplier.SeccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.SupplierLicenses.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new SupplierLicenseDto(
            entity.Id, entity.Seq, entity.SupplierId, entity.LicenseNumber, entity.LicenseType,
            entity.Remarks, entity.IssueDate, entity.ExpiryDate, entity.ErpCode, entity.CreatedOn);
    }
}
