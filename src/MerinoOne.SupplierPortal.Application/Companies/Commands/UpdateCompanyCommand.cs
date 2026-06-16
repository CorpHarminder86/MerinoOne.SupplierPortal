using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Companies.Commands;

/// <summary>Tenant-Admin: rename / re-code a company in the current tenant.</summary>
public record UpdateCompanyCommand(Guid Id, string Code, string Name) : IRequest<Unit>;

public class UpdateCompanyCommandValidator : AbstractValidator<UpdateCompanyCommand>
{
    public UpdateCompanyCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public class UpdateCompanyCommandHandler : IRequestHandler<UpdateCompanyCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateCompanyCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<Unit> Handle(UpdateCompanyCommand request, CancellationToken ct)
    {
        var code = request.Code.Trim();
        var name = request.Name.Trim();

        // Tenant-filtered (no IgnoreQueryFilters) — a Tenant Admin can only touch its own tenant's rows.
        var entity = await _db.TenantEntities.FirstOrDefaultAsync(e => e.Id == request.Id, ct)
            ?? throw new NotFoundException("Company", request.Id);

        if (!string.Equals(entity.Code, code, StringComparison.Ordinal))
        {
            var dup = await _db.TenantEntities
                .AnyAsync(e => e.Id != entity.Id && e.TenantId == entity.TenantId && e.Code == code, ct);
            if (dup)
                throw new ConflictException($"Company code '{code}' already exists in this tenant.");
        }

        var now = DateTime.UtcNow;
        entity.Code = code;
        entity.Name = name;
        entity.UpdatedBy = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        entity.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
