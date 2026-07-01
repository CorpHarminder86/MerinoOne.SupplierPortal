using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Masters;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Masters.Settings;

// ====================================================================================================
// R5 (TSD R5 Addendum §5, §6.1 / Component 1 / [[r5-consolidation]]) — the named, ERP-mappable ship-to addresses
// (admin.CompanyAddress) that hang directly off a company = admin.TenantEntity. (The duplicate admin.Company was
// dropped; the company list/CRUD itself is the pre-existing CompaniesController over TenantEntity.) These addresses
// are ADMIN-OWNED CONFIG MASTERS — maintained under Settings (Settings.Read / Settings.Write), tenant-scoped.
//
// CompanyAddress carries NO tenant/seccode column of its own (AuditableEntity); it scopes via the owning
// TenantEntity. Reads/writes therefore run IgnoreQueryFilters() and re-scope by resolving/joining the owning
// TenantEntity's TenantId (mirrors AttachmentTypeHandlers' hand-applied tenant guard). The request/DTO CompanyId
// field is that company's id — a TenantEntity id.
//
// erpCode uniqueness (§4.2/§5.3): a runtime "exists within the tenant entity" check mirrors the filtered unique
// index UQ_CompanyAddress_tenantEntity_erp (the DB backstop), so a clear 409 is returned before the index throws.
// ====================================================================================================

public record GetCompanyAddressesQuery(Guid CompanyId, bool? IsActive = null) : IRequest<List<CompanyAddressDto>>;

public class GetCompanyAddressesQueryHandler : IRequestHandler<GetCompanyAddressesQuery, List<CompanyAddressDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetCompanyAddressesQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<List<CompanyAddressDto>> Handle(GetCompanyAddressesQuery request, CancellationToken ct)
    {
        // CompanyAddress scopes via its owning TenantEntity — resolve that tenant-scoped first, then list addresses.
        var tid = _user.TenantId;
        var companyExists = await _db.TenantEntities.IgnoreQueryFilters()
            .AnyAsync(c => c.Id == request.CompanyId && !c.IsDeleted && (tid == null || c.TenantId == tid), ct);
        if (!companyExists) throw new NotFoundException("Company", request.CompanyId);

        var q = _db.CompanyAddresses.IgnoreQueryFilters()
            .Where(a => a.TenantEntityId == request.CompanyId && !a.IsDeleted);
        if (request.IsActive.HasValue) q = q.Where(a => a.IsActive == request.IsActive.Value);
        return await q.OrderBy(a => a.AddressName)
            .Select(a => new CompanyAddressDto(
                a.Id, a.Seq, a.TenantEntityId, a.AddressName, a.ErpCode, a.AddressType,
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
        // The owning company IS a TenantEntity — resolve it tenant-scoped (bypass the caller's seccode RLS).
        _ = await _db.TenantEntities.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == request.Body.CompanyId && !c.IsDeleted && (tid == null || c.TenantId == tid), ct)
            ?? throw new NotFoundException("Company", request.Body.CompanyId);

        var erpCode = string.IsNullOrWhiteSpace(request.Body.ErpCode) ? null : request.Body.ErpCode.Trim();
        await GuardErpCodeUniqueAsync(erpCode, request.Body.CompanyId, excludeId: null, ct);

        var entity = new CompanyAddress
        {
            Id = Guid.NewGuid(),
            TenantEntityId = request.Body.CompanyId,
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

    private async Task GuardErpCodeUniqueAsync(string? erpCode, Guid tenantEntityId, Guid? excludeId, CancellationToken ct)
    {
        if (erpCode is null) return;   // NULL erpCode never collides (filtered unique index).
        // Mirrors UQ_CompanyAddress_tenantEntity_erp (erpCode unique per tenant entity when present, !isDeleted).
        // Case-insensitive because the inbound ship-to resolution matches case-insensitively (§6.2).
        var clash = await _db.CompanyAddresses.IgnoreQueryFilters()
            .AnyAsync(a => a.TenantEntityId == tenantEntityId && !a.IsDeleted
                && (excludeId == null || a.Id != excludeId)
                && a.ErpCode != null && a.ErpCode.ToLower() == erpCode.ToLower(), ct);
        if (clash) throw new ConflictException($"ERP code '{erpCode}' is already used by another address in this company.");
    }

    internal static CompanyAddressDto Map(CompanyAddress a) => new(
        a.Id, a.Seq, a.TenantEntityId, a.AddressName, a.ErpCode, a.AddressType,
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
        // Resolve the address joined to its owning tenant-scoped TenantEntity (CompanyAddress has no tenant column).
        var a = await (from addr in _db.CompanyAddresses.IgnoreQueryFilters()
                       join te in _db.TenantEntities.IgnoreQueryFilters() on addr.TenantEntityId equals te.Id
                       where addr.Id == request.Id && !addr.IsDeleted && !te.IsDeleted && (tid == null || te.TenantId == tid)
                       select addr).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("CompanyAddress", request.Id);

        var erpCode = string.IsNullOrWhiteSpace(request.Body.ErpCode) ? null : request.Body.ErpCode.Trim();
        if (erpCode is not null)
        {
            var clash = await _db.CompanyAddresses.IgnoreQueryFilters()
                .AnyAsync(x => x.TenantEntityId == a.TenantEntityId && !x.IsDeleted && x.Id != a.Id
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
