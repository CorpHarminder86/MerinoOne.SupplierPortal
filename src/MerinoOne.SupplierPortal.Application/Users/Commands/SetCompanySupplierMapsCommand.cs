using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Users.Commands;

/// <summary>
/// Enhancement Round 2 / Feature B — bulk supplier map/unmap as a unified diff for ONE company.
/// Given the desired set of suppliers (all of which must belong to <paramref name="TenantEntityId"/>),
/// reconciles the user's ACTIVE supplier maps in that company:
///   • add    desired-not-existing  (reuse <see cref="SupplierMapService.CreateOrRestoreMapAsync"/>),
///   • remove existing-not-desired  (reuse <see cref="SupplierMapService.RemoveMapAsync"/>),
///   • update kept rows whose <c>SecRight.CanWrite</c> != requested.
/// An empty <paramref name="SupplierIds"/> removes ALL maps for this company only (other companies are
/// untouched). The company <see cref="Domain.Entities.Admin.UserCompanyMap"/> (AllSuppliers=false) is
/// auto-created once if absent. The whole reconciliation runs in a single transaction.
/// </summary>
public record SetCompanySupplierMapsCommand(Guid UserId, Guid TenantEntityId, Guid[] SupplierIds, bool CanWrite)
    : IRequest<Unit>;

public class SetCompanySupplierMapsCommandValidator : AbstractValidator<SetCompanySupplierMapsCommand>
{
    public SetCompanySupplierMapsCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.TenantEntityId).NotEmpty();
        // SupplierIds MAY be empty (= remove all maps for this company).
    }
}

public class SetCompanySupplierMapsCommandHandler : IRequestHandler<SetCompanySupplierMapsCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SupplierMapService _maps;

    public SetCompanySupplierMapsCommandHandler(IAppDbContext db, ICurrentUser user, SupplierMapService maps)
    {
        _db = db;
        _user = user;
        _maps = maps;
    }

    public async Task<Unit> Handle(SetCompanySupplierMapsCommand request, CancellationToken ct)
    {
        var user = await _db.AppUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
            ?? throw new NotFoundException("User", request.UserId);

        // Company must exist; its tenant must equal the acting tenant (mirrors MapSupplierCommand).
        var company = await _db.TenantEntities.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => !e.IsDeleted && e.Id == request.TenantEntityId, ct)
            ?? throw new NotFoundException("Company", request.TenantEntityId);

        var actingTenant = _user.TenantId;
        if (actingTenant.HasValue && company.TenantId != actingTenant.Value)
            throw new ConflictException("The selected company belongs to a different tenant.");

        var desiredIds = request.SupplierIds.Distinct().ToList();

        // Validate every requested supplier exists and belongs to THIS company (reject cross-company).
        var desiredSuppliers = await _db.Suppliers.IgnoreQueryFilters()
            .Where(s => !s.IsDeleted && desiredIds.Contains(s.Id))
            .ToListAsync(ct);

        var byId = desiredSuppliers.ToDictionary(s => s.Id);
        foreach (var id in desiredIds)
        {
            if (!byId.TryGetValue(id, out var sup))
                throw new NotFoundException("Supplier", id);
            if (sup.TenantEntityId != request.TenantEntityId)
                throw new ConflictException(
                    $"Supplier '{sup.SupplierCode}' does not belong to the selected company. A supplier maps to exactly one company.");
        }

        // Load the user's ACTIVE supplier maps whose supplier belongs to this company (join on company).
        var existing = await (
            from m in _db.SupplierUserMaps.IgnoreQueryFilters()
            where m.AppUserId == user.Id && !m.IsDeleted
            join s in _db.Suppliers.IgnoreQueryFilters().Where(s => !s.IsDeleted && s.TenantEntityId == request.TenantEntityId)
                on m.SupplierId equals s.Id
            select m)
            .ToListAsync(ct);

        var existingBySupplier = existing.ToDictionary(m => m.SupplierId);
        var desiredSet = desiredIds.ToHashSet();

        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var now = DateTime.UtcNow;

        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            // ADD: desired suppliers without an active map → create-or-restore.
            foreach (var id in desiredIds)
            {
                if (existingBySupplier.ContainsKey(id))
                    continue;
                await _maps.CreateOrRestoreMapAsync(user, byId[id], request.CanWrite, actor, now, ct);
            }

            // REMOVE: active maps not in the desired set → remove map + paired SecRight.
            foreach (var map in existing)
            {
                if (desiredSet.Contains(map.SupplierId))
                    continue;
                await _maps.RemoveMapAsync(map, ct);
            }

            // UPDATE: kept maps whose SecRight.CanWrite differs from the requested value.
            var keptSecRightIds = existing
                .Where(m => desiredSet.Contains(m.SupplierId))
                .Select(m => m.SecRightId)
                .Distinct()
                .ToList();

            if (keptSecRightIds.Count > 0)
            {
                var secRights = await _db.SecRights.IgnoreQueryFilters()
                    .Where(r => keptSecRightIds.Contains(r.Id) && !r.IsDeleted)
                    .ToListAsync(ct);

                foreach (var r in secRights)
                {
                    if (r.CanWrite != request.CanWrite)
                    {
                        r.CanWrite = request.CanWrite;
                        r.UpdatedBy = actor;
                        r.UpdatedOn = now;
                    }
                }
            }

            // Auto-create the supplier-derived company map (AllSuppliers=false) once if absent. Even an
            // empty desired set leaves the company map in place — removing all suppliers doesn't revoke
            // the user's company access (that's RemoveUserCompanyCommand's job).
            await _maps.EnsureCompanyMapAsync(user, company, actor, now, ct);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return Unit.Value;
    }
}
