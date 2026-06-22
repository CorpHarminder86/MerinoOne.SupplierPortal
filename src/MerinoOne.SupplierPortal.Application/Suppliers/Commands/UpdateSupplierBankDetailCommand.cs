using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

public record UpdateSupplierBankDetailCommand(Guid SupplierId, Guid BankDetailId, UpdateSupplierBankDetailRequest Body)
    : IRequest<SupplierBankDetailDto>;

public class UpdateSupplierBankDetailCommandValidator : AbstractValidator<UpdateSupplierBankDetailCommand>
{
    public UpdateSupplierBankDetailCommandValidator()
    {
        SupplierBankValidationRules.Apply(
            this,
            x => x.Body.BankName, x => x.Body.BankAddress, x => x.Body.AccountName,
            x => x.Body.AccountNumber, x => x.Body.CurrencyId, x => x.Body.IfscCode, x => x.Body.SwiftCode);
    }
}

public class UpdateSupplierBankDetailCommandHandler : IRequestHandler<UpdateSupplierBankDetailCommand, SupplierBankDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public UpdateSupplierBankDetailCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<SupplierBankDetailDto> Handle(UpdateSupplierBankDetailCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        var entity = await _db.SupplierBankDetails
            .FirstOrDefaultAsync(b => b.Id == request.BankDetailId && b.SupplierId == supplier.Id, ct)
            ?? throw new NotFoundException("SupplierBankDetail", request.BankDetailId);

        var currency = await _db.Currencies.FirstOrDefaultAsync(c => c.Id == request.Body.CurrencyId, ct)
                       ?? throw new ValidationException(new Dictionary<string, string[]>
                       {
                           ["currencyId"] = new[] { "Currency not found." }
                       });
        SupplierBankValidationRules.AssertIfscConditional(currency.Code, request.Body.IfscCode);

        if (request.Body.IsPrimary && !entity.IsPrimary)
        {
            var others = await _db.SupplierBankDetails
                .Where(b => b.SupplierId == supplier.Id && b.Id != entity.Id && b.IsPrimary).ToListAsync(ct);
            foreach (var o in others) o.IsPrimary = false;
        }

        entity.BankName = request.Body.BankName.Trim();
        entity.BankAddress = request.Body.BankAddress.Trim();
        entity.AccountName = request.Body.AccountName.Trim();
        entity.AccountNumber = request.Body.AccountNumber.Trim();
        entity.CurrencyId = request.Body.CurrencyId;
        entity.IfscCode = (request.Body.IfscCode ?? string.Empty).Trim().ToUpperInvariant();
        entity.SwiftCode = string.IsNullOrWhiteSpace(request.Body.SwiftCode) ? null : request.Body.SwiftCode.Trim().ToUpperInvariant();
        entity.IsPrimary = request.Body.IsPrimary;
        entity.UpdatedBy = _user.UserCode;
        entity.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new SupplierBankDetailDto(
            entity.Id, entity.Seq, entity.SupplierId, entity.BankName, entity.BankAddress,
            entity.AccountName, entity.AccountNumber, entity.CurrencyId, currency.Code,
            entity.IfscCode, entity.SwiftCode, entity.IsPrimary, entity.ErpCode, entity.CreatedOn);
    }
}
