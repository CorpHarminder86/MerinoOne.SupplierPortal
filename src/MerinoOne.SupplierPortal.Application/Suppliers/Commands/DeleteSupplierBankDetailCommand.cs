using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

/// <summary>Soft-deletes a supplier bank account (the AuditableEntityInterceptor flips IsDeleted).</summary>
public record DeleteSupplierBankDetailCommand(Guid SupplierId, Guid BankDetailId) : IRequest<Unit>;

public class DeleteSupplierBankDetailCommandHandler : IRequestHandler<DeleteSupplierBankDetailCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public DeleteSupplierBankDetailCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<Unit> Handle(DeleteSupplierBankDetailCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        var entity = await _db.SupplierBankDetails
            .FirstOrDefaultAsync(b => b.Id == request.BankDetailId && b.SupplierId == supplier.Id, ct)
            ?? throw new NotFoundException("SupplierBankDetail", request.BankDetailId);

        _db.SupplierBankDetails.Remove(entity);   // soft-delete via the audit interceptor.
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
