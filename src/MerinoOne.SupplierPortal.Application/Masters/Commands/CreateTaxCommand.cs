using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Commands;

/// <summary>
/// Creates a Tax master row (Module 6). Cloned end-to-end from <c>CreateDeliveryTermCommand</c>: Tax is
/// <c>ICompanyScoped</c> (sharing-aware), so the company filter on the dup-check + the read path is sharing-aware
/// and the <c>ScopeStampInterceptor</c> stamps <c>TenantEntityId</c> from the active company on insert.
/// </summary>
public record CreateTaxCommand(CreateTaxRequest Body) : IRequest<TaxDto>;

public class CreateTaxCommandValidator : AbstractValidator<CreateTaxCommand>
{
    public CreateTaxCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Description).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.TaxRate).GreaterThanOrEqualTo(0).When(x => x.Body.TaxRate.HasValue);
    }
}

public class CreateTaxCommandHandler : IRequestHandler<CreateTaxCommand, TaxDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public CreateTaxCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<TaxDto> Handle(CreateTaxCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        var exists = await _db.Taxes.AnyAsync(t => t.Code == code, ct);
        if (exists) throw new ConflictException($"Tax with code '{code}' already exists.");

        var entity = new Tax
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = request.Body.Description.Trim(),
            TaxRate = request.Body.TaxRate,
            IsActive = request.Body.IsActive,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Taxes.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new TaxDto(entity.Id, entity.Seq, entity.Code, entity.Description, entity.TaxRate, entity.IsActive, entity.CreatedOn);
    }
}
