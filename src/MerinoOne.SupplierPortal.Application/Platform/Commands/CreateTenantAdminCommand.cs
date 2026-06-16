using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Platform;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Platform.Commands;

/// <summary>
/// Platform-Admin onboarding: create the tenant's FIRST Tenant Admin user. The user is stamped with the
/// target TenantId (cross-tenant — stamped explicitly, not via the interceptor), granted the "Admin"
/// role, given a server-generated temp password and MustChangePassword = true. The default EmailTemplate
/// set is cloned into the new tenant (so the tenant starts with editable templates).
/// </summary>
public record CreateTenantAdminCommand(CreateTenantAdminRequest Body) : IRequest<CreateTenantAdminResultDto>;

public class CreateTenantAdminCommandValidator : AbstractValidator<CreateTenantAdminCommand>
{
    public CreateTenantAdminCommandValidator()
    {
        RuleFor(x => x.Body.TenantId).NotEmpty();
        RuleFor(x => x.Body.UserCode)
            .NotEmpty().Length(3, 50)
            .Matches("^[a-zA-Z0-9_]+$").WithMessage("UserCode must be alphanumeric or underscore.");
        RuleFor(x => x.Body.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}

public class CreateTenantAdminCommandHandler : IRequestHandler<CreateTenantAdminCommand, CreateTenantAdminResultDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IPasswordHasher _hasher;

    public CreateTenantAdminCommandHandler(IAppDbContext db, ICurrentUser user, IPasswordHasher hasher)
    {
        _db = db;
        _user = user;
        _hasher = hasher;
    }

    public async Task<CreateTenantAdminResultDto> Handle(CreateTenantAdminCommand request, CancellationToken ct)
    {
        var body = request.Body;
        var email = body.Email.Trim().ToLowerInvariant();
        var userCode = body.UserCode.Trim();

        var tenant = await _db.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => !t.IsDeleted && t.Id == body.TenantId, ct)
            ?? throw new NotFoundException("Tenant", body.TenantId);

        // Email + userCode stay GLOBALLY unique (1 user = 1 tenant).
        if (await _db.AppUsers.IgnoreQueryFilters().AnyAsync(u => u.UserCode == userCode, ct))
            throw new ConflictException($"UserCode '{userCode}' is already in use.");
        if (await _db.AppUsers.IgnoreQueryFilters().AnyAsync(u => u.Email == email, ct))
            throw new ConflictException($"Email '{email}' is already in use.");

        // The "Admin" (Tenant Admin) role. Roles are per-tenant; resolve the row owned by the target
        // tenant, falling back to a TenantId-null seed row if a per-tenant copy hasn't been created yet
        // (phase 2b's TenantSeeder will own per-tenant role provisioning).
        var adminRole = await _db.Roles.IgnoreQueryFilters()
            .Where(r => !r.IsDeleted && r.Name == "Admin")
            .Where(r => r.TenantId == tenant.Id || r.TenantId == null)
            .OrderByDescending(r => r.TenantId == tenant.Id) // prefer the tenant-owned row
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Role", "Admin");

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "platform" : _user.UserCode;
        var userId = Guid.NewGuid();
        var tempPassword = PasswordGenerator.Generate();

        _db.AppUsers.Add(new AppUser
        {
            Id = userId,
            TenantId = tenant.Id, // explicit cross-tenant stamp
            UserCode = userCode,
            FullName = body.FullName.Trim(),
            Email = email,
            PasswordHash = _hasher.Hash(tempPassword),
            IsInternal = true,
            IsMfaEnabled = false,
            IsActive = true,
            MustChangePassword = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            AppUserId = userId,
            RoleId = adminRole.Id,
            CreatedBy = actor,
            CreatedOn = now
        });

        // Default U-seccode + self SecRight — mirror CreateUserCommand / UserSeeder so the new admin
        // is a first-class principal.
        var seccodeId = Guid.NewGuid();
        _db.Seccodes.Add(new Seccode
        {
            Id = seccodeId,
            SeccodeType = SeccodeType.U,
            Name = userCode + " default",
            AppUserId = userId,
            CreatedBy = actor,
            CreatedOn = now
        });
        _db.SecRights.Add(new SecRight
        {
            Id = Guid.NewGuid(),
            SeccodeId = seccodeId,
            UserCode = userCode,
            CanRead = true,
            CanWrite = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        // Clone the default EmailTemplate set into the new tenant if it has none yet. Trivial enough to
        // do inline: copy the canonical specs as fresh per-tenant rows. (Phase 2b's TenantSeeder may take
        // over richer per-tenant template provisioning; this guarantees the tenant is never template-less.)
        await CloneDefaultEmailTemplatesAsync(tenant.Id, actor, now, ct);

        await _db.SaveChangesAsync(ct);

        return new CreateTenantAdminResultDto(userId, userCode, email, tempPassword);
    }

    private async Task CloneDefaultEmailTemplatesAsync(Guid tenantId, string actor, DateTime now, CancellationToken ct)
    {
        var alreadyHas = await _db.EmailTemplates.IgnoreQueryFilters()
            .AnyAsync(t => t.TenantId == tenantId, ct);
        if (alreadyHas) return;

        // Prefer cloning an existing global / seed-tenant template set (preserves any admin edits to the
        // canonical copies); fall back to nothing if none exist (phase 2b seeder will backfill).
        var source = await _db.EmailTemplates.IgnoreQueryFilters()
            .Where(t => !t.IsDeleted)
            .ToListAsync(ct);

        // Deduplicate by templateKey, preferring a row that already belongs to no tenant (the canonical set).
        var byKey = source
            .GroupBy(t => t.TemplateKey)
            .Select(g => g.OrderBy(t => t.TenantId.HasValue ? 1 : 0).First());

        foreach (var t in byKey)
        {
            _db.EmailTemplates.Add(new EmailTemplate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                TemplateKey = t.TemplateKey,
                Subject = t.Subject,
                HtmlBody = t.HtmlBody,
                IsActive = t.IsActive,
                Notes = t.Notes,
                CreatedBy = actor,
                CreatedOn = now
            });
        }
    }
}
