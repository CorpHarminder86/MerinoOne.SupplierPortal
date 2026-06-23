using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

/// <summary>
/// Internal user sets a supplier's commercial terms (R4 #1) — Currency + Payment/Delivery term FKs, snapshotting
/// the term codes (mirrors PurchaseOrder's dual FK+code pattern; PaymentTerm/DeliveryTerm are ICompanyScoped so
/// a term may belong to an unshared source company — resolve the code via IgnoreQueryFilters within the tenant).
/// </summary>
public record UpdateSupplierCommercialTermsCommand(Guid SupplierId, UpdateCommercialTermsRequest Body) : IRequest<Unit>;

public class UpdateSupplierCommercialTermsCommandHandler : IRequestHandler<UpdateSupplierCommercialTermsCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdateSupplierCommercialTermsCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<Unit> Handle(UpdateSupplierCommercialTermsCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        var b = request.Body;

        supplier.CurrencyId = b.CurrencyId;

        supplier.PaymentTermId = b.PaymentTermId;
        supplier.PaymentTermCode = b.PaymentTermId.HasValue
            ? await _db.PaymentTerms.IgnoreQueryFilters().Where(t => t.Id == b.PaymentTermId.Value)
                .Select(t => t.Code).FirstOrDefaultAsync(ct)
            : null;

        supplier.DeliveryTermId = b.DeliveryTermId;
        supplier.DeliveryTermCode = b.DeliveryTermId.HasValue
            ? await _db.DeliveryTerms.IgnoreQueryFilters().Where(t => t.Id == b.DeliveryTermId.Value)
                .Select(t => t.Code).FirstOrDefaultAsync(ct)
            : null;

        supplier.UpdatedBy = _user.UserCode;
        supplier.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
