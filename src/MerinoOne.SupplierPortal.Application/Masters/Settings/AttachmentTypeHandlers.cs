using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Doc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Settings;

// ====================================================================================================
// R4 (2026-06-26) — Phase 5a, TSD R4 Addendum §8.5 (Component 5). The admin Settings CRUD surface for the
// attachment-type catalogue + the read-only attachment-entity reference list. Admin-gated (Settings.Read /
// Settings.Write). Tenant-scoped: UQ (tenantId, code) filtered isDeleted=0. No hard delete — deactivate.
//
// Reads run under an Admin/SuperAdmin principal (Settings.* is admin-only) which is privileged for the seccode
// RLS filter, so the seeded tenant-admin-owned config rows are visible WITHOUT IgnoreQueryFilters; the always-on
// tenant filter still scopes them to the caller's tenant. Writes own new rows by the seccode of an existing
// tenant config row (the tenant-admin seccode the seeder used) so the FK is always valid + ownership consistent.
// ====================================================================================================

internal static class SettingsSeccodeResolver
{
    /// <summary>
    /// Resolves the seccode that owns this tenant's attachment-governance config rows: reuse an existing
    /// AttachmentType / AttachmentEntity / policy row's seccode (the tenant-admin seccode the seeder set). The
    /// seeder runs before any admin can reach these endpoints, so one of these always exists for an onboarded
    /// tenant. Filter-bypassed read scoped by tenant (config master, not seccode-owned by the caller).
    /// </summary>
    public static async Task<Guid> ResolveAttachmentConfigSeccodeAsync(IAppDbContext db, Guid? tenantId, CancellationToken ct)
    {
        var fromEntity = await db.AttachmentEntities.IgnoreQueryFilters()
            .Where(e => tenantId == null || e.TenantId == tenantId)
            .Select(e => (Guid?)e.SeccodeId).FirstOrDefaultAsync(ct);
        if (fromEntity is Guid s1 && s1 != Guid.Empty) return s1;

        var fromType = await db.AttachmentTypes.IgnoreQueryFilters()
            .Where(t => tenantId == null || t.TenantId == tenantId)
            .Select(t => (Guid?)t.SeccodeId).FirstOrDefaultAsync(ct);
        if (fromType is Guid s2 && s2 != Guid.Empty) return s2;

        throw new ConflictException(
            "Attachment governance is not seeded for this tenant; cannot resolve the owning seccode.");
    }
}

// ---------------- AttachmentType ----------------

public record GetAttachmentTypesQuery(bool? IsActive = null) : IRequest<List<AttachmentTypeDto>>;

public class GetAttachmentTypesQueryHandler : IRequestHandler<GetAttachmentTypesQuery, List<AttachmentTypeDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetAttachmentTypesQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<AttachmentTypeDto>> Handle(GetAttachmentTypesQuery request, CancellationToken ct)
    {
        // Tenant-wide config master — read tenant-scoped, bypassing the caller's seccode RLS (see GetEntities).
        var tid = _user.TenantId;
        var q = _db.AttachmentTypes.IgnoreQueryFilters().Where(t => !t.IsDeleted && (tid == null || t.TenantId == tid));
        if (request.IsActive.HasValue) q = q.Where(t => t.IsActive == request.IsActive.Value);
        return await q.OrderBy(t => t.Code)
            .Select(t => new AttachmentTypeDto(t.Id, t.Seq, t.Code, t.Name, t.IsActive, t.CreatedOn))
            .ToListAsync(ct);
    }
}

public record CreateAttachmentTypeCommand(CreateAttachmentTypeRequest Body) : IRequest<AttachmentTypeDto>;

public class CreateAttachmentTypeCommandValidator : AbstractValidator<CreateAttachmentTypeCommand>
{
    public CreateAttachmentTypeCommandValidator()
    {
        RuleFor(x => x.Body.Code).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(100);
    }
}

public class CreateAttachmentTypeCommandHandler : IRequestHandler<CreateAttachmentTypeCommand, AttachmentTypeDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public CreateAttachmentTypeCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<AttachmentTypeDto> Handle(CreateAttachmentTypeCommand request, CancellationToken ct)
    {
        var code = request.Body.Code.Trim();
        var tid = _user.TenantId;
        // Unique per tenant (the filtered UQ allows re-adding a soft-deleted code; the runtime check mirrors it).
        // Tenant-scoped IgnoreQueryFilters — the config rows are owned by the config seccode, not the caller.
        var exists = await _db.AttachmentTypes.IgnoreQueryFilters()
            .AnyAsync(t => t.Code == code && !t.IsDeleted && (tid == null || t.TenantId == tid), ct);
        if (exists) throw new ConflictException($"Attachment type with code '{code}' already exists.");

        var seccodeId = await SettingsSeccodeResolver.ResolveAttachmentConfigSeccodeAsync(_db, _user.TenantId, ct);
        var entity = new AttachmentType
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = request.Body.Name.Trim(),
            IsActive = true,
            SeccodeId = seccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.AttachmentTypes.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new AttachmentTypeDto(entity.Id, entity.Seq, entity.Code, entity.Name, entity.IsActive, entity.CreatedOn);
    }
}

public record UpdateAttachmentTypeCommand(Guid Id, UpdateAttachmentTypeRequest Body) : IRequest<AttachmentTypeDto>;

public class UpdateAttachmentTypeCommandValidator : AbstractValidator<UpdateAttachmentTypeCommand>
{
    public UpdateAttachmentTypeCommandValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(100);
    }
}

public class UpdateAttachmentTypeCommandHandler : IRequestHandler<UpdateAttachmentTypeCommand, AttachmentTypeDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdateAttachmentTypeCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<AttachmentTypeDto> Handle(UpdateAttachmentTypeCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var t = await _db.AttachmentTypes.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted && (tid == null || x.TenantId == tid), ct)
                ?? throw new NotFoundException("AttachmentType", request.Id);

        // Code is immutable (it aligns with DocumentUpload.documentType); only Name / IsActive are editable.
        t.Name = request.Body.Name.Trim();
        t.IsActive = request.Body.IsActive;
        t.UpdatedBy = _user.UserCode;
        t.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new AttachmentTypeDto(t.Id, t.Seq, t.Code, t.Name, t.IsActive, t.CreatedOn);
    }
}

// ---------------- AttachmentEntity (read-only) ----------------

public record GetAttachmentEntitiesQuery(bool? IsActive = null) : IRequest<List<AttachmentEntityDto>>;

public class GetAttachmentEntitiesQueryHandler : IRequestHandler<GetAttachmentEntitiesQuery, List<AttachmentEntityDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetAttachmentEntitiesQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<AttachmentEntityDto>> Handle(GetAttachmentEntitiesQuery request, CancellationToken ct)
    {
        // Tenant-wide config master (seeded under the tenant-admin seccode) — read tenant-scoped, bypassing the
        // caller's seccode RLS (no admin owns a SecRight on the config seccode, so the plain filter returned empty).
        var tid = _user.TenantId;
        var q = _db.AttachmentEntities.IgnoreQueryFilters().Where(e => !e.IsDeleted && (tid == null || e.TenantId == tid));
        if (request.IsActive.HasValue) q = q.Where(e => e.IsActive == request.IsActive.Value);
        return await q.OrderBy(e => e.Code)
            .Select(e => new AttachmentEntityDto(e.Id, e.Seq, e.Code, e.Name, e.IsActive, e.CreatedOn))
            .ToListAsync(ct);
    }
}
