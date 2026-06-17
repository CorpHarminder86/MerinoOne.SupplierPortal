using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Users;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

public record MapSupplierCommand(Guid UserId, MapSupplierRequest Body) : IRequest<Unit>;

public class MapSupplierCommandValidator : AbstractValidator<MapSupplierCommand>
{
    public MapSupplierCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Body.SupplierId).NotEmpty();
        RuleFor(x => x.Body.TenantEntityId).NotEmpty()
            .WithMessage("A company (TenantEntityId) is required — map the user under the supplier's company.");
    }
}

public class MapSupplierCommandHandler : IRequestHandler<MapSupplierCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierMapService _maps;

    public MapSupplierCommandHandler(IAppDbContext db, ICurrentUser user, SupplierMapService maps)
    {
        _db = db;
        _user = user;
        _maps = maps;
    }

    public async Task<Unit> Handle(MapSupplierCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
            ?? throw new NotFoundException("User", request.UserId);

        var supplier = await _db.Suppliers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == request.Body.SupplierId, ct)
            ?? throw new NotFoundException("Supplier", request.Body.SupplierId);

        // === Company scoping (TenantCompany §5) ============================================
        // The mapping is made under a specific company; the supplier MUST belong to that company.
        // This closes the "supplier spanning companies" hole — a supplier lives in exactly one company.
        var companyId = request.Body.TenantEntityId;

        // Company must exist; its tenant must equal the acting tenant. IgnoreQueryFilters + explicit
        // tenant restriction so a cross-tenant company can never be referenced.
        var actingTenant = _user.TenantId;
        var company = await _db.TenantEntities.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => !e.IsDeleted && e.Id == companyId, ct)
            ?? throw new NotFoundException("Company", companyId);

        if (actingTenant.HasValue && company.TenantId != actingTenant.Value)
            throw new ConflictException("The selected company belongs to a different tenant.");

        if (supplier.TenantEntityId != companyId)
            throw new ConflictException(
                $"Supplier '{supplier.SupplierCode}' does not belong to the selected company. A supplier maps to exactly one company.");

        // Load any existing ACTIVE mapping — an active row is a true conflict. (The create-or-restore
        // logic in SupplierMapService re-loads the row incl. soft-deleted and restores it.)
        var activeMap = await _db.SupplierUserMaps.IgnoreQueryFilters()
            .AnyAsync(m => m.AppUserId == user.Id && m.SupplierId == supplier.Id && !m.IsDeleted, ct);

        if (activeMap)
            throw new ConflictException($"User '{user.UserCode}' is already mapped to supplier '{supplier.SupplierCode}'.");

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        // Create-or-restore the SupplierUserMap + paired SecRight (shared with the bulk command).
        await _maps.CreateOrRestoreMapAsync(user, supplier, request.Body.CanWrite, actor, now, ct);

        // (c) Auto-grant supplier-derived company access (AllSuppliers=false) if missing; restoring a
        //     soft-deleted map never downgrades an existing AllSuppliers=true grant.
        await _maps.EnsureCompanyMapAsync(user, company, actor, now, ct);

        // Single SaveChangesAsync — EF wraps in an implicit transaction.
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
