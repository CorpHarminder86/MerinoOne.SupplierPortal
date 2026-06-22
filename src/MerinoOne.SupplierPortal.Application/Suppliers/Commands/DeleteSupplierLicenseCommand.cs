using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

/// <summary>Soft-deletes a supplier license (the AuditableEntityInterceptor flips IsDeleted).</summary>
public record DeleteSupplierLicenseCommand(Guid SupplierId, Guid LicenseId) : IRequest<Unit>;

public class DeleteSupplierLicenseCommandHandler : IRequestHandler<DeleteSupplierLicenseCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public DeleteSupplierLicenseCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<Unit> Handle(DeleteSupplierLicenseCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        var entity = await _db.SupplierLicenses
            .FirstOrDefaultAsync(l => l.Id == request.LicenseId && l.SupplierId == supplier.Id, ct)
            ?? throw new NotFoundException("SupplierLicense", request.LicenseId);

        _db.SupplierLicenses.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
