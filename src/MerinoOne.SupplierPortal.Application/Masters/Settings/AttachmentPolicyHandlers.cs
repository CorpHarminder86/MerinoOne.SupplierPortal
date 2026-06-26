using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Settings;

// ====================================================================================================
// R4 (2026-06-26) — Phase 5a, TSD R4 Addendum §8.5 + decision D5 (Component 5). The admin Settings CRUD surface
// for the TWO-TIER attachment requirement policy grid. Admin-gated (Settings.Read / Settings.Write).
//
// GET ?entityCode=&supplierId= → the policy rows for that entity: tenant defaults (supplierId NULL) and, when a
// supplierId is given, that supplier's overrides. Each row carries the EFFECTIVE requirement (D5 supplier-wins:
// supplier override ?? tenant default ?? Optional) per type so the grid can show the resolved value.
//
// PUT upserts a row keyed on the appropriate D5 unique (tenant-default vs supplier-override). DELETE removes one.
// Reads run privileged (Settings.* admin-only) so the seeded tenant-admin-owned rows are visible without
// IgnoreQueryFilters; the always-on tenant filter still scopes them.
// ====================================================================================================

// ---------------- GET : the policy grid for an entity (+ optional supplier) ----------------

public record GetAttachmentPoliciesQuery(string EntityCode, Guid? SupplierId) : IRequest<List<AttachmentPolicyDto>>;

public class GetAttachmentPoliciesQueryHandler : IRequestHandler<GetAttachmentPoliciesQuery, List<AttachmentPolicyDto>>
{
    private readonly IAppDbContext _db;
    public GetAttachmentPoliciesQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<AttachmentPolicyDto>> Handle(GetAttachmentPoliciesQuery request, CancellationToken ct)
    {
        var entityCode = request.EntityCode.Trim();
        var entity = await _db.AttachmentEntities
            .Where(e => e.Code == entityCode)
            .Select(e => new { e.Id, e.Code })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("AttachmentEntity", entityCode);

        // Tenant defaults (supplierId NULL) + (when given) THIS supplier's overrides. Join the type for code/name.
        var rows = await (
            from p in _db.AttachmentRequirementPolicies
            join t in _db.AttachmentTypes on p.AttachmentTypeId equals t.Id
            where p.AttachmentEntityId == entity.Id
                  && (p.SupplierId == null || p.SupplierId == request.SupplierId)
            select new
            {
                p.Id,
                p.AttachmentTypeId,
                TypeCode = t.Code,
                TypeName = t.Name,
                p.SupplierId,
                p.Requirement,
                p.IsActive,
            }).ToListAsync(ct);

        // Effective per type (D5 supplier-wins): supplier override ?? tenant default ?? Optional.
        var effectiveByType = rows
            .GroupBy(r => r.AttachmentTypeId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var supplierRow = g.FirstOrDefault(x => x.SupplierId.HasValue && x.IsActive);
                    if (supplierRow is not null) return supplierRow.Requirement;
                    var tenantRow = g.FirstOrDefault(x => !x.SupplierId.HasValue && x.IsActive);
                    return tenantRow?.Requirement ?? AttachmentRequirement.Optional;
                });

        return rows
            .OrderBy(r => r.TypeCode)
            .ThenBy(r => r.SupplierId.HasValue)   // tenant default first, then supplier override
            .Select(r => new AttachmentPolicyDto(
                r.Id, entity.Code, r.AttachmentTypeId, r.TypeCode, r.TypeName,
                r.SupplierId, r.Requirement.ToString(), effectiveByType[r.AttachmentTypeId].ToString(), r.IsActive))
            .ToList();
    }
}

// ---------------- PUT : upsert a policy row (tenant default or supplier override) ----------------

public record UpsertAttachmentPolicyCommand(UpsertAttachmentPolicyRequest Body) : IRequest<AttachmentPolicyDto>;

public class UpsertAttachmentPolicyCommandValidator : AbstractValidator<UpsertAttachmentPolicyCommand>
{
    public UpsertAttachmentPolicyCommandValidator()
    {
        RuleFor(x => x.Body.AttachmentEntityCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.AttachmentTypeCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.Requirement)
            .NotEmpty()
            .Must(r => Enum.TryParse<AttachmentRequirement>(r, ignoreCase: true, out _))
            .WithMessage("Requirement must be one of Mandatory, Warning, Optional.");
    }
}

public class UpsertAttachmentPolicyCommandHandler : IRequestHandler<UpsertAttachmentPolicyCommand, AttachmentPolicyDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpsertAttachmentPolicyCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<AttachmentPolicyDto> Handle(UpsertAttachmentPolicyCommand request, CancellationToken ct)
    {
        var b = request.Body;
        var entityCode = b.AttachmentEntityCode.Trim();
        var typeCode = b.AttachmentTypeCode.Trim();
        var requirement = Enum.Parse<AttachmentRequirement>(b.Requirement, ignoreCase: true);

        var entity = await _db.AttachmentEntities.FirstOrDefaultAsync(e => e.Code == entityCode, ct)
                     ?? throw new NotFoundException("AttachmentEntity", entityCode);
        var type = await _db.AttachmentTypes.FirstOrDefaultAsync(t => t.Code == typeCode, ct)
                   ?? throw new NotFoundException("AttachmentType", typeCode);

        if (b.SupplierId is Guid sid)
        {
            var supplierExists = await _db.Suppliers.AnyAsync(s => s.Id == sid, ct);
            if (!supplierExists) throw new NotFoundException("Supplier", sid);
        }

        // Upsert by the D5 unique: (entity, type) for a tenant default, (supplier, entity, type) for an override.
        var row = await _db.AttachmentRequirementPolicies.FirstOrDefaultAsync(
            p => p.AttachmentEntityId == entity.Id
                 && p.AttachmentTypeId == type.Id
                 && p.SupplierId == b.SupplierId, ct);

        if (row is null)
        {
            // Own the new row by the same seccode the seeded config rows use (tenant-admin seccode).
            var seccodeId = await SettingsSeccodeResolver.ResolveAttachmentConfigSeccodeAsync(_db, _user.TenantId, ct);
            row = new AttachmentRequirementPolicy
            {
                Id = Guid.NewGuid(),
                AttachmentEntityId = entity.Id,
                AttachmentTypeId = type.Id,
                SupplierId = b.SupplierId,
                Requirement = requirement,
                IsActive = true,
                SeccodeId = seccodeId,
                CreatedBy = _user.UserCode,
                CreatedOn = DateTime.UtcNow,
            };
            _db.AttachmentRequirementPolicies.Add(row);
        }
        else
        {
            row.Requirement = requirement;
            row.IsActive = true;   // a re-upsert re-activates a previously-deactivated row.
            row.UpdatedBy = _user.UserCode;
            row.UpdatedOn = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);

        // The effective value for a single returned row mirrors the grid resolver (supplier override wins).
        var effective = row.SupplierId.HasValue
            ? requirement
            : await ResolveEffectiveAsync(entity.Id, type.Id, ct);

        return new AttachmentPolicyDto(
            row.Id, entity.Code, type.Id, type.Code, type.Name,
            row.SupplierId, requirement.ToString(), effective.ToString(), row.IsActive);
    }

    private async Task<AttachmentRequirement> ResolveEffectiveAsync(Guid entityId, Guid typeId, CancellationToken ct)
    {
        // No supplier context on a tenant-default upsert — effective = the tenant default itself.
        var tenant = await _db.AttachmentRequirementPolicies
            .Where(p => p.AttachmentEntityId == entityId && p.AttachmentTypeId == typeId
                        && p.SupplierId == null && p.IsActive)
            .Select(p => (AttachmentRequirement?)p.Requirement)
            .FirstOrDefaultAsync(ct);
        return tenant ?? AttachmentRequirement.Optional;
    }
}

// ---------------- DELETE : remove a policy row ----------------

public record DeleteAttachmentPolicyCommand(Guid Id) : IRequest<Unit>;

public class DeleteAttachmentPolicyCommandHandler : IRequestHandler<DeleteAttachmentPolicyCommand, Unit>
{
    private readonly IAppDbContext _db;
    public DeleteAttachmentPolicyCommandHandler(IAppDbContext db) => _db = db;

    public async Task<Unit> Handle(DeleteAttachmentPolicyCommand request, CancellationToken ct)
    {
        var row = await _db.AttachmentRequirementPolicies.FirstOrDefaultAsync(p => p.Id == request.Id, ct)
                  ?? throw new NotFoundException("AttachmentRequirementPolicy", request.Id);
        // Soft-delete (interceptor); a removed row reverts to the tenant default / Optional fallback.
        _db.AttachmentRequirementPolicies.Remove(row);
        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
