using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Settings;

// ====================================================================================================
// R5 (TSD R5 Addendum §5, §6.1 / Component 1) — Company Master CRUD. The CUSTOMER (buying entity) the supplier
// ships to (admin.Company, 1:1 to tenantEntityId) and its named, ERP-mappable ship-to addresses
// (admin.CompanyAddress). These are ADMIN-OWNED CONFIG MASTERS — maintained under Settings (Settings.Read /
// Settings.Write), tenant-scoped, exactly like the AttachmentType catalogue (see AttachmentTypeHandlers).
//
// Tenant-scoping mirrors AttachmentTypeHandlers verbatim: reads/writes run IgnoreQueryFilters() with a
// hand-applied (tid == null || x.TenantId == tid) + !IsDeleted guard, because the config rows are owned by the
// tenant-admin seccode (CompanySeeder), not by the calling admin's seccode — so the always-on seccode RLS filter
// would otherwise return them empty. New rows are owned by the tenant-admin config seccode the same way (via the
// resolver below), keeping the seccode FK valid and ownership consistent.
//
// erpCode uniqueness (§4.2/§5.3): a runtime "exists within the company" check mirrors the filtered unique index
// UQ_CompanyAddress_company_erp (the DB backstop), so a clear 409 is returned before the index would throw.
// ====================================================================================================

internal static class CompanyMasterSeccodeResolver
{
    /// <summary>
    /// Resolves the seccode that owns this tenant's Company-master config rows: prefer an existing Company row's
    /// seccode (the tenant-admin seccode CompanySeeder set), else fall back to the attachment-governance config
    /// seccode (the SAME tenant-admin seccode — both seeders own their rows by it). Filter-bypassed read scoped by
    /// tenant (config master, not seccode-owned by the caller).
    /// </summary>
    public static async Task<Guid> ResolveAsync(IAppDbContext db, Guid? tenantId, CancellationToken ct)
    {
        var fromCompany = await db.Companies.IgnoreQueryFilters()
            .Where(c => tenantId == null || c.TenantId == tenantId)
            .Select(c => (Guid?)c.SeccodeId).FirstOrDefaultAsync(ct);
        if (fromCompany is Guid s1 && s1 != Guid.Empty) return s1;

        // No Company yet for this tenant (first create before the seeder, or a fresh tenant): reuse the tenant-admin
        // seccode that already owns the attachment-governance config (seeded under the same principal).
        return await SettingsSeccodeResolver.ResolveAttachmentConfigSeccodeAsync(db, tenantId, ct);
    }
}

// ============================== Company ==============================

public record GetCompaniesQuery(bool? IsActive = null) : IRequest<List<CompanyMasterDto>>;

public class GetCompaniesQueryHandler : IRequestHandler<GetCompaniesQuery, List<CompanyMasterDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetCompaniesQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<CompanyMasterDto>> Handle(GetCompaniesQuery request, CancellationToken ct)
    {
        // Tenant-wide config master — read tenant-scoped, bypassing the caller's seccode RLS (see AttachmentType).
        var tid = _user.TenantId;
        var q = _db.Companies.IgnoreQueryFilters().Where(c => !c.IsDeleted && (tid == null || c.TenantId == tid));
        if (request.IsActive.HasValue) q = q.Where(c => c.IsActive == request.IsActive.Value);
        return await q.OrderBy(c => c.Name)
            .Select(c => new CompanyMasterDto(c.Id, c.Seq, c.TenantEntityId, c.Name, c.IsActive, c.CreatedOn))
            .ToListAsync(ct);
    }
}

// Create a Company-master row for the caller's ACTIVE company (tenantEntityId = the active company). One Company
// per (tenant, tenantEntityId) — UQ_Company_tenant_entity is the DB backstop; the runtime check mirrors it.
public record CreateCompanyCommand(CreateCompanyMasterRequest Body) : IRequest<CompanyMasterDto>;

public class CreateCompanyCommandValidator : AbstractValidator<CreateCompanyCommand>
{
    public CreateCompanyCommandValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(300);
    }
}

public class CreateCompanyCommandHandler : IRequestHandler<CreateCompanyCommand, CompanyMasterDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ICurrentCompany _company;
    public CreateCompanyCommandHandler(IAppDbContext db, ICurrentUser user, ICurrentCompany company)
    { _db = db; _user = user; _company = company; }

    public async Task<CompanyMasterDto> Handle(CreateCompanyCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var tenantEntityId = _company.ActiveCompanyId
            ?? throw new ConflictException("The current session has no active company context.");

        var exists = await _db.Companies.IgnoreQueryFilters()
            .AnyAsync(c => c.TenantEntityId == tenantEntityId && !c.IsDeleted && (tid == null || c.TenantId == tid), ct);
        if (exists) throw new ConflictException("A Company already exists for the active company (one per buying entity).");

        var seccodeId = await CompanyMasterSeccodeResolver.ResolveAsync(_db, tid, ct);
        var entity = new Company
        {
            Id = Guid.NewGuid(),
            TenantId = tid,
            TenantEntityId = tenantEntityId,
            Name = request.Body.Name.Trim(),
            IsActive = true,
            SeccodeId = seccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.Companies.Add(entity);
        await _db.SaveChangesAsync(ct);
        return new CompanyMasterDto(entity.Id, entity.Seq, entity.TenantEntityId, entity.Name, entity.IsActive, entity.CreatedOn);
    }
}

public record UpdateCompanyCommand(Guid Id, UpdateCompanyMasterRequest Body) : IRequest<CompanyMasterDto>;

public class UpdateCompanyCommandValidator : AbstractValidator<UpdateCompanyCommand>
{
    public UpdateCompanyCommandValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(300);
    }
}

public class UpdateCompanyCommandHandler : IRequestHandler<UpdateCompanyCommand, CompanyMasterDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdateCompanyCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<CompanyMasterDto> Handle(UpdateCompanyCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var c = await _db.Companies.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == request.Id && !x.IsDeleted && (tid == null || x.TenantId == tid), ct)
                ?? throw new NotFoundException("Company", request.Id);

        c.Name = request.Body.Name.Trim();
        c.IsActive = request.Body.IsActive;
        c.UpdatedBy = _user.UserCode;
        c.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new CompanyMasterDto(c.Id, c.Seq, c.TenantEntityId, c.Name, c.IsActive, c.CreatedOn);
    }
}

// ============================== CompanyAddress ==============================

public record GetCompanyAddressesQuery(Guid CompanyId, bool? IsActive = null) : IRequest<List<CompanyAddressDto>>;

public class GetCompanyAddressesQueryHandler : IRequestHandler<GetCompanyAddressesQuery, List<CompanyAddressDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetCompanyAddressesQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<CompanyAddressDto>> Handle(GetCompanyAddressesQuery request, CancellationToken ct)
    {
        // CompanyAddress carries no tenant column of its own (it scopes via the owning Company), so we scope by the
        // owning Company's tenant: resolve the company tenant-scoped first, then list its addresses.
        var tid = _user.TenantId;
        var companyExists = await _db.Companies.IgnoreQueryFilters()
            .AnyAsync(c => c.Id == request.CompanyId && !c.IsDeleted && (tid == null || c.TenantId == tid), ct);
        if (!companyExists) throw new NotFoundException("Company", request.CompanyId);

        var q = _db.CompanyAddresses.IgnoreQueryFilters()
            .Where(a => a.CompanyId == request.CompanyId && !a.IsDeleted);
        if (request.IsActive.HasValue) q = q.Where(a => a.IsActive == request.IsActive.Value);
        return await q.OrderBy(a => a.AddressName)
            .Select(a => new CompanyAddressDto(
                a.Id, a.Seq, a.CompanyId, a.AddressName, a.ErpCode, a.AddressType,
                a.AddressLine1, a.AddressLine2, a.City, a.State, a.Pincode, a.Country, a.IsActive, a.CreatedOn))
            .ToListAsync(ct);
    }
}

public record CreateCompanyAddressCommand(CreateCompanyAddressRequest Body) : IRequest<CompanyAddressDto>;

public class CreateCompanyAddressCommandValidator : AbstractValidator<CreateCompanyAddressCommand>
{
    public CreateCompanyAddressCommandValidator()
    {
        RuleFor(x => x.Body.CompanyId).NotEmpty();
        RuleFor(x => x.Body.AddressName).NotEmpty().MaximumLength(150);   // §4.2/§5 — REQUIRED.
        RuleFor(x => x.Body.ErpCode).MaximumLength(50);                   // optional; uniqueness checked at runtime.
        RuleFor(x => x.Body.AddressType).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.AddressLine1).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Body.AddressLine2).MaximumLength(300);
        RuleFor(x => x.Body.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.State).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.Pincode).MaximumLength(20);
        RuleFor(x => x.Body.Country).MaximumLength(100);
    }
}

public class CreateCompanyAddressCommandHandler : IRequestHandler<CreateCompanyAddressCommand, CompanyAddressDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public CreateCompanyAddressCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<CompanyAddressDto> Handle(CreateCompanyAddressCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var company = await _db.Companies.IgnoreQueryFilters()
                          .FirstOrDefaultAsync(c => c.Id == request.Body.CompanyId && !c.IsDeleted && (tid == null || c.TenantId == tid), ct)
                      ?? throw new NotFoundException("Company", request.Body.CompanyId);

        var erpCode = string.IsNullOrWhiteSpace(request.Body.ErpCode) ? null : request.Body.ErpCode.Trim();
        await GuardErpCodeUniqueAsync(erpCode, request.Body.CompanyId, excludeId: null, ct);

        var entity = new CompanyAddress
        {
            Id = Guid.NewGuid(),
            CompanyId = request.Body.CompanyId,
            AddressName = request.Body.AddressName.Trim(),
            ErpCode = erpCode,
            AddressType = request.Body.AddressType.Trim(),
            AddressLine1 = request.Body.AddressLine1.Trim(),
            AddressLine2 = request.Body.AddressLine2?.Trim(),
            City = request.Body.City.Trim(),
            State = request.Body.State.Trim(),
            Pincode = request.Body.Pincode?.Trim(),
            Country = string.IsNullOrWhiteSpace(request.Body.Country) ? "India" : request.Body.Country.Trim(),
            IsActive = true,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };
        _db.CompanyAddresses.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    private async Task GuardErpCodeUniqueAsync(string? erpCode, Guid companyId, Guid? excludeId, CancellationToken ct)
    {
        if (erpCode is null) return;   // NULL erpCode never collides (filtered unique index).
        // Mirrors UQ_CompanyAddress_company_erp (erpCode unique per company when present, !isDeleted). Case-insensitive
        // because the inbound ship-to resolution matches case-insensitively (§6.2).
        var clash = await _db.CompanyAddresses.IgnoreQueryFilters()
            .AnyAsync(a => a.CompanyId == companyId && !a.IsDeleted
                && (excludeId == null || a.Id != excludeId)
                && a.ErpCode != null && a.ErpCode.ToLower() == erpCode.ToLower(), ct);
        if (clash) throw new ConflictException($"ERP code '{erpCode}' is already used by another address in this company.");
    }

    internal static CompanyAddressDto Map(CompanyAddress a) => new(
        a.Id, a.Seq, a.CompanyId, a.AddressName, a.ErpCode, a.AddressType,
        a.AddressLine1, a.AddressLine2, a.City, a.State, a.Pincode, a.Country, a.IsActive, a.CreatedOn);
}

public record UpdateCompanyAddressCommand(Guid Id, UpdateCompanyAddressRequest Body) : IRequest<CompanyAddressDto>;

public class UpdateCompanyAddressCommandValidator : AbstractValidator<UpdateCompanyAddressCommand>
{
    public UpdateCompanyAddressCommandValidator()
    {
        RuleFor(x => x.Body.AddressName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Body.ErpCode).MaximumLength(50);
        RuleFor(x => x.Body.AddressType).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.AddressLine1).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Body.AddressLine2).MaximumLength(300);
        RuleFor(x => x.Body.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.State).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.Pincode).MaximumLength(20);
        RuleFor(x => x.Body.Country).MaximumLength(100);
    }
}

public class UpdateCompanyAddressCommandHandler : IRequestHandler<UpdateCompanyAddressCommand, CompanyAddressDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public UpdateCompanyAddressCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<CompanyAddressDto> Handle(UpdateCompanyAddressCommand request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        // Resolve the address joined to its tenant-scoped Company (CompanyAddress has no tenant column of its own).
        var a = await (from addr in _db.CompanyAddresses.IgnoreQueryFilters()
                       join comp in _db.Companies.IgnoreQueryFilters() on addr.CompanyId equals comp.Id
                       where addr.Id == request.Id && !addr.IsDeleted && !comp.IsDeleted && (tid == null || comp.TenantId == tid)
                       select addr).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("CompanyAddress", request.Id);

        var erpCode = string.IsNullOrWhiteSpace(request.Body.ErpCode) ? null : request.Body.ErpCode.Trim();
        if (erpCode is not null)
        {
            var clash = await _db.CompanyAddresses.IgnoreQueryFilters()
                .AnyAsync(x => x.CompanyId == a.CompanyId && !x.IsDeleted && x.Id != a.Id
                    && x.ErpCode != null && x.ErpCode.ToLower() == erpCode.ToLower(), ct);
            if (clash) throw new ConflictException($"ERP code '{erpCode}' is already used by another address in this company.");
        }

        a.AddressName = request.Body.AddressName.Trim();
        a.ErpCode = erpCode;
        a.AddressType = request.Body.AddressType.Trim();
        a.AddressLine1 = request.Body.AddressLine1.Trim();
        a.AddressLine2 = request.Body.AddressLine2?.Trim();
        a.City = request.Body.City.Trim();
        a.State = request.Body.State.Trim();
        a.Pincode = request.Body.Pincode?.Trim();
        a.Country = string.IsNullOrWhiteSpace(request.Body.Country) ? "India" : request.Body.Country.Trim();
        a.IsActive = request.Body.IsActive;
        a.UpdatedBy = _user.UserCode;
        a.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return CreateCompanyAddressCommandHandler.Map(a);
    }
}
