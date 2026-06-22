using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

/// <summary>
/// Adds a bank account to a supplier (Module 1). <see cref="SupplierBankDetail"/> is a seccode-protected
/// <c>BaseAggregateRoot</c>: <c>Owner</c> (SeccodeId) is stamped to the supplier's G-seccode on create, and
/// <see cref="SupplierWriteGuard"/> enforces <c>SecRight.canWrite</c> before the mutation.
/// </summary>
public record AddSupplierBankDetailCommand(Guid SupplierId, AddSupplierBankDetailRequest Body) : IRequest<SupplierBankDetailDto>;

public class AddSupplierBankDetailCommandValidator : AbstractValidator<AddSupplierBankDetailCommand>
{
    public AddSupplierBankDetailCommandValidator()
    {
        SupplierBankValidationRules.Apply(
            this,
            x => x.Body.BankName, x => x.Body.BankAddress, x => x.Body.AccountName,
            x => x.Body.AccountNumber, x => x.Body.CurrencyId, x => x.Body.IfscCode, x => x.Body.SwiftCode);
    }
}

public class AddSupplierBankDetailCommandHandler : IRequestHandler<AddSupplierBankDetailCommand, SupplierBankDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierWriteGuard _guard;

    public AddSupplierBankDetailCommandHandler(IAppDbContext db, ICurrentUser user, SupplierWriteGuard guard)
    {
        _db = db; _user = user; _guard = guard;
    }

    public async Task<SupplierBankDetailDto> Handle(AddSupplierBankDetailCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        await _guard.EnsureCanWriteAsync(supplier.Id, supplier.SeccodeId, ct);

        var currency = await _db.Currencies.FirstOrDefaultAsync(c => c.Id == request.Body.CurrencyId, ct)
                       ?? throw new ValidationException(new Dictionary<string, string[]>
                       {
                           ["currencyId"] = new[] { "Currency not found." }
                       });

        // IFSC is conditionally REQUIRED for INR (domestic); optional for foreign currencies (SWIFT covers wires).
        SupplierBankValidationRules.AssertIfscConditional(currency.Code, request.Body.IfscCode);

        // First account auto-promotes to primary; an explicit primary demotes the rest.
        var existing = await _db.SupplierBankDetails.Where(b => b.SupplierId == supplier.Id).ToListAsync(ct);
        var isPrimary = request.Body.IsPrimary || existing.Count == 0;
        if (isPrimary)
            foreach (var e in existing.Where(e => e.IsPrimary)) e.IsPrimary = false;

        var entity = new SupplierBankDetail
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            BankName = request.Body.BankName.Trim(),
            BankAddress = request.Body.BankAddress.Trim(),
            AccountName = request.Body.AccountName.Trim(),
            AccountNumber = request.Body.AccountNumber.Trim(),
            CurrencyId = request.Body.CurrencyId,
            IfscCode = (request.Body.IfscCode ?? string.Empty).Trim().ToUpperInvariant(),
            SwiftCode = string.IsNullOrWhiteSpace(request.Body.SwiftCode) ? null : request.Body.SwiftCode.Trim().ToUpperInvariant(),
            IsPrimary = isPrimary,
            SeccodeId = supplier.SeccodeId,   // Owner = supplier's G-seccode (seccode RLS).
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.SupplierBankDetails.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new SupplierBankDetailDto(
            entity.Id, entity.Seq, entity.SupplierId, entity.BankName, entity.BankAddress,
            entity.AccountName, entity.AccountNumber, entity.CurrencyId, currency.Code,
            entity.IfscCode, entity.SwiftCode, entity.IsPrimary, entity.ErpCode, entity.CreatedOn);
    }
}

/// <summary>Shared FluentValidation + conditional-IFSC rules for the add/update bank-detail commands.</summary>
public static class SupplierBankValidationRules
{
    // Standard Indian IFSC: 4 alpha bank code + '0' + 6 alphanumeric branch code.
    public static readonly Regex IfscPattern = new("^[A-Z]{4}0[A-Z0-9]{6}$", RegexOptions.Compiled);

    public static void Apply<T>(
        AbstractValidator<T> v,
        System.Linq.Expressions.Expression<Func<T, string>> bankName,
        System.Linq.Expressions.Expression<Func<T, string>> bankAddress,
        System.Linq.Expressions.Expression<Func<T, string>> accountName,
        System.Linq.Expressions.Expression<Func<T, string>> accountNumber,
        System.Linq.Expressions.Expression<Func<T, Guid>> currencyId,
        System.Linq.Expressions.Expression<Func<T, string>> ifsc,
        System.Linq.Expressions.Expression<Func<T, string?>> swift)
    {
        v.RuleFor(bankName).NotEmpty().MaximumLength(200);
        v.RuleFor(bankAddress).NotEmpty().MaximumLength(500);
        v.RuleFor(accountName).NotEmpty().MaximumLength(200);
        v.RuleFor(accountNumber).NotEmpty().MaximumLength(64);
        v.RuleFor(currencyId).NotEmpty().WithMessage("Currency is required.");
        // Format check only when present; the INR-required-ness is asserted in the handler (needs the currency code).
        v.RuleFor(ifsc)
            .Must(code => string.IsNullOrWhiteSpace(code) || IfscPattern.IsMatch(code.Trim().ToUpperInvariant()))
            .WithMessage("IFSC must match ^[A-Z]{4}0[A-Z0-9]{6}$.");
        v.RuleFor(swift).MaximumLength(20);
    }

    /// <summary>For INR accounts the IFSC is mandatory (and must match the format). Throws a 400 ValidationException.</summary>
    public static void AssertIfscConditional(string? currencyCode, string? ifsc)
    {
        var isInr = string.Equals(currencyCode?.Trim(), "INR", StringComparison.OrdinalIgnoreCase);
        var trimmed = (ifsc ?? string.Empty).Trim();
        if (isInr && string.IsNullOrWhiteSpace(trimmed))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["ifscCode"] = new[] { "IFSC is required for INR (domestic) accounts." }
            });
        if (!string.IsNullOrWhiteSpace(trimmed) && !IfscPattern.IsMatch(trimmed.ToUpperInvariant()))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["ifscCode"] = new[] { "IFSC must match ^[A-Z]{4}0[A-Z0-9]{6}$." }
            });
    }
}
