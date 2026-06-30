using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.StatusMapping;
using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Settings;

// ====================================================================================================
// R5 (TSD R5 Addendum §4.7 / §11 — Component 7) — ERP→portal PO-status mapping CRUD. Admin config master
// maintained under Settings (Settings.Read / Settings.Write), tenant-scoped, exactly like the AttachmentType
// catalogue + the Company master. Tenant-scoping mirrors those handlers: reads/writes run IgnoreQueryFilters()
// with a hand-applied (tid == null || x.TenantId == tid) + !IsDeleted guard, because the rows are owned by the
// tenant-admin config seccode (PoStatusMappingSeeder), not by the calling admin's seccode.
//
// Validation (§11.2): the target poStatus MUST be one of the ERP-driven subset — Draft | Released | Cancelled |
// Closed | Delivered. The supplier/fulfilment-driven statuses (Acknowledged / Accepted / DateProposed / Rejected
// / PartiallyDelivered / FullyShipped) are NOT valid targets (a mapped Accepted would bypass the supplier's
// confirmation). erpStatus is unique per tenant (case-insensitive — the inbound resolution matches CI, §4.7),
// mirroring the filtered unique index UQ_PoStatusMapping_tenant_erp.
//
// Every mutation fans the cache invalidation out so the singleton PoStatusMapService reloads on the next inbound
// resolution (same contract as the SystemSettings save/reset handlers).
// ====================================================================================================

/// <summary>The ERP-driven portal-status subset that is valid as a mapping TARGET (§11.2 / UC-SM-07).</summary>
public static class PoStatusMappingTargets
{
    public static readonly IReadOnlyList<PoStatus> Allowed = new[]
    {
        PoStatus.Draft, PoStatus.Released, PoStatus.Cancelled, PoStatus.Closed, PoStatus.Delivered,
    };

    public static bool IsValidTarget(string? value)
        => Enum.TryParse<PoStatus>((value ?? string.Empty).Trim(), ignoreCase: true, out var s) && Allowed.Contains(s);

    /// <summary>Canonicalises the target to the enum name (so case is normalised before persist).</summary>
    public static string Canonical(string value) => Enum.Parse<PoStatus>(value.Trim(), ignoreCase: true).ToString();
}

public record GetPoStatusMappingsQuery(bool? IsActive = null) : IRequest<List<PoStatusMappingDto>>;

public class GetPoStatusMappingsQueryHandler : IRequestHandler<GetPoStatusMappingsQuery, List<PoStatusMappingDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetPoStatusMappingsQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<PoStatusMappingDto>> Handle(GetPoStatusMappingsQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var q = _db.PoStatusMappings.IgnoreQueryFilters().Where(m => !m.IsDeleted && (tid == null || m.TenantId == tid));
        if (request.IsActive.HasValue) q = q.Where(m => m.IsActive == request.IsActive.Value);
        return await q.OrderBy(m => m.PoStatus).ThenBy(m => m.ErpStatus)
            .Select(m => new PoStatusMappingDto(m.Id, m.Seq, m.ErpStatus, m.PoStatus, m.IsActive, m.CreatedOn))
            .ToListAsync(ct);
    }
}

public record CreatePoStatusMappingCommand(CreatePoStatusMappingRequest Body) : IRequest<PoStatusMappingDto>;

public class CreatePoStatusMappingCommandValidator : AbstractValidator<CreatePoStatusMappingCommand>
{
    public CreatePoStatusMappingCommandValidator()
    {
        RuleFor(x => x.Body.ErpStatus).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.PoStatus).NotEmpty().MaximumLength(50)
            .Must(PoStatusMappingTargets.IsValidTarget)
            .WithMessage("poStatus must be one of the ERP-driven targets: Draft, Released, Cancelled, Closed, Delivered.");
    }
}

public class CreatePoStatusMappingCommandHandler : IRequestHandler<CreatePoStatusMappingCommand, PoStatusMappingDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IEnumerable<ISettingsCacheInvalidator> _invalidators;
    public CreatePoStatusMappingCommandHandler(IAppDbContext db, ICurrentUser user, IEnumerable<ISettingsCacheInvalidator> invalidators)
    { _db = db; _user = user; _invalidators = invalidators; }

    public async Task<PoStatusMappingDto> Handle(CreatePoStatusMappingCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var erp = request.Body.ErpStatus.Trim();

        // erpStatus unique per tenant (case-insensitive — the inbound resolution is CI). Mirrors the filtered UQ.
        var clash = await _db.PoStatusMappings.IgnoreQueryFilters()
            .AnyAsync(m => !m.IsDeleted && (tid == null || m.TenantId == tid) && m.ErpStatus.ToLower() == erp.ToLower(), ct);
        if (clash) throw new ConflictException($"ERP status '{erp}' is already mapped for this tenant.");

        var seccodeId = await PoStatusMappingSeccodeResolver.ResolveAsync(_db, tid, ct);
        var entity = new PoStatusMapping
        {
            Id = Guid.NewGuid(),
            TenantId = tid,
            ErpStatus = erp,
            PoStatus = PoStatusMappingTargets.Canonical(request.Body.PoStatus),
            IsActive = true,
            SeccodeId = seccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.PoStatusMappings.Add(entity);
        await _db.SaveChangesAsync(ct);
        Invalidate();
        return new PoStatusMappingDto(entity.Id, entity.Seq, entity.ErpStatus, entity.PoStatus, entity.IsActive, entity.CreatedOn);
    }

    private void Invalidate() { foreach (var inv in _invalidators) inv.InvalidateCategory(PoStatusMapService.Category); }
}

public record UpdatePoStatusMappingCommand(Guid Id, UpdatePoStatusMappingRequest Body) : IRequest<PoStatusMappingDto>;

public class UpdatePoStatusMappingCommandValidator : AbstractValidator<UpdatePoStatusMappingCommand>
{
    public UpdatePoStatusMappingCommandValidator()
    {
        RuleFor(x => x.Body.PoStatus).NotEmpty().MaximumLength(50)
            .Must(PoStatusMappingTargets.IsValidTarget)
            .WithMessage("poStatus must be one of the ERP-driven targets: Draft, Released, Cancelled, Closed, Delivered.");
    }
}

public class UpdatePoStatusMappingCommandHandler : IRequestHandler<UpdatePoStatusMappingCommand, PoStatusMappingDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IEnumerable<ISettingsCacheInvalidator> _invalidators;
    public UpdatePoStatusMappingCommandHandler(IAppDbContext db, ICurrentUser user, IEnumerable<ISettingsCacheInvalidator> invalidators)
    { _db = db; _user = user; _invalidators = invalidators; }

    public async Task<PoStatusMappingDto> Handle(UpdatePoStatusMappingCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var m = await _db.PoStatusMappings.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted && (tid == null || x.TenantId == tid), ct)
                ?? throw new NotFoundException("PoStatusMapping", request.Id);

        // erpStatus is immutable (it is the lookup key). Only the target poStatus + active flag are editable.
        m.PoStatus = PoStatusMappingTargets.Canonical(request.Body.PoStatus);
        m.IsActive = request.Body.IsActive;
        m.UpdatedBy = _user.UserCode;
        m.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        Invalidate();
        return new PoStatusMappingDto(m.Id, m.Seq, m.ErpStatus, m.PoStatus, m.IsActive, m.CreatedOn);
    }

    private void Invalidate() { foreach (var inv in _invalidators) inv.InvalidateCategory(PoStatusMapService.Category); }
}

// Soft-delete (deactivate) — no hard delete, consistent with the other config masters. The row is marked
// IsDeleted so the filtered UQ frees the erpStatus for a future re-add.
public record DeletePoStatusMappingCommand(Guid Id) : IRequest<Unit>;

public class DeletePoStatusMappingCommandHandler : IRequestHandler<DeletePoStatusMappingCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IEnumerable<ISettingsCacheInvalidator> _invalidators;
    public DeletePoStatusMappingCommandHandler(IAppDbContext db, ICurrentUser user, IEnumerable<ISettingsCacheInvalidator> invalidators)
    { _db = db; _user = user; _invalidators = invalidators; }

    public async Task<Unit> Handle(DeletePoStatusMappingCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var m = await _db.PoStatusMappings.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted && (tid == null || x.TenantId == tid), ct)
                ?? throw new NotFoundException("PoStatusMapping", request.Id);

        m.IsActive = false;
        m.IsDeleted = true;
        m.DeletedOn = DateTime.UtcNow;
        m.DeletedBy = _user.UserCode;
        await _db.SaveChangesAsync(ct);
        foreach (var inv in _invalidators) inv.InvalidateCategory(PoStatusMapService.Category);
        return Unit.Value;
    }
}

internal static class PoStatusMappingSeccodeResolver
{
    /// <summary>
    /// Resolves the seccode that owns this tenant's PoStatusMapping config rows: reuse an existing mapping row's
    /// seccode (the tenant-admin seccode the seeder set), else fall back to the attachment-governance config
    /// seccode (the SAME tenant-admin seccode). Filter-bypassed read scoped by tenant (config master, not
    /// seccode-owned by the caller).
    /// </summary>
    public static async Task<Guid> ResolveAsync(IAppDbContext db, Guid? tenantId, CancellationToken ct)
    {
        var fromMapping = await db.PoStatusMappings.IgnoreQueryFilters()
            .Where(m => tenantId == null || m.TenantId == tenantId)
            .Select(m => (Guid?)m.SeccodeId).FirstOrDefaultAsync(ct);
        if (fromMapping is Guid s1 && s1 != Guid.Empty) return s1;

        return await SettingsSeccodeResolver.ResolveAttachmentConfigSeccodeAsync(db, tenantId, ct);
    }
}
