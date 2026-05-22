using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.Commands;

public record ApproveSupplierCommand(Guid SupplierId, ApproveSupplierRequest Body) : IRequest<Unit>;

public class ApproveSupplierCommandValidator : AbstractValidator<ApproveSupplierCommand> { /* business validation done in handler — needs DB */ }

public class ApproveSupplierCommandHandler : IRequestHandler<ApproveSupplierCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IPasswordHasher _hasher;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly ILogger<ApproveSupplierCommandHandler> _logger;

    public ApproveSupplierCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        IPasswordHasher hasher,
        IEmailService email,
        IConfiguration config,
        ILogger<ApproveSupplierCommandHandler> logger)
    {
        _db = db;
        _user = user;
        _hasher = hasher;
        _email = email;
        _config = config;
        _logger = logger;
    }

    public async Task<Unit> Handle(ApproveSupplierCommand request, CancellationToken ct)
    {
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == request.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", request.SupplierId);

        // Latest verification per type (GST/PAN/MSME); fail-state requires override comment
        var latestByType = await _db.SupplierVerifications
            .Where(v => v.SupplierId == request.SupplierId)
            .GroupBy(v => v.VerificationType)
            .Select(g => g.OrderByDescending(v => v.AttemptedAt).First())
            .ToListAsync(ct);

        var anyFail = latestByType.Any(v => v.Result == VerificationResult.Fail);
        if (anyFail && string.IsNullOrWhiteSpace(request.Body.OverrideComment))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["approvalOverrideComment"] = new[] { "Override comment is required when any NIC verification is Fail." }
            });
        }

        var now = DateTime.UtcNow;
        supplier.RegistrationStatus = RegistrationStatus.Approved;
        supplier.IsActiveSupplier = true;
        supplier.ApprovedAt = now;
        supplier.ApprovedBy = _user.UserCode;
        supplier.ApprovalOverrideComment = request.Body.OverrideComment;

        // ---- Auto-provision the primary contact as a portal user ----
        var primaryContact = await _db.SupplierContacts
            .Where(c => c.SupplierId == supplier.Id && c.IsPrimary)
            .OrderBy(c => c.CreatedOn)
            .FirstOrDefaultAsync(ct);

        if (primaryContact == null)
        {
            primaryContact = await _db.SupplierContacts
                .Where(c => c.SupplierId == supplier.Id)
                .OrderBy(c => c.CreatedOn)
                .FirstOrDefaultAsync(ct);
        }

        string? oneTimePassword = null;
        AppUser? newUser = null;

        if (primaryContact != null)
        {
            var contactEmail = primaryContact.Email.Trim().ToLowerInvariant();

            var emailExists = await _db.AppUsers.IgnoreQueryFilters()
                .AnyAsync(u => u.Email.ToLower() == contactEmail, ct);

            if (!emailExists)
            {
                var baseCode = $"sup-{supplier.SupplierCode}".ToLowerInvariant();
                var userCode = await ResolveUniqueUserCodeAsync(baseCode, ct);
                var actor = string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;

                oneTimePassword = PasswordGenerator.Generate();

                var userId = Guid.NewGuid();
                newUser = new AppUser
                {
                    Id = userId,
                    UserCode = userCode,
                    FullName = primaryContact.ContactName,
                    Email = contactEmail,
                    PasswordHash = _hasher.Hash(oneTimePassword),
                    IsInternal = false,
                    IsMfaEnabled = false,
                    IsActive = true,
                    MustChangePassword = true,
                    CreatedBy = actor,
                    CreatedOn = now,
                };
                _db.AppUsers.Add(newUser);

                // Assign Supplier role
                var supplierRoleId = await _db.Roles.IgnoreQueryFilters()
                    .Where(r => r.Name == "Supplier")
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(ct);
                if (supplierRoleId.HasValue)
                {
                    _db.UserRoles.Add(new UserRole
                    {
                        Id = Guid.NewGuid(),
                        AppUserId = userId,
                        RoleId = supplierRoleId.Value,
                        CreatedBy = actor,
                        CreatedOn = now,
                    });
                }
                else
                {
                    _logger.LogWarning("Role 'Supplier' not found during auto-provision of {UserCode}; user will have no role.", userCode);
                }

                // Type-U seccode + self SecRight (mirrors UserSeeder / CreateUserCommand)
                var userSeccodeId = Guid.NewGuid();
                _db.Seccodes.Add(new Seccode
                {
                    Id = userSeccodeId,
                    SeccodeType = SeccodeType.U,
                    Name = userCode + " default",
                    AppUserId = userId,
                    CreatedBy = actor,
                    CreatedOn = now,
                });
                _db.SecRights.Add(new SecRight
                {
                    Id = Guid.NewGuid(),
                    SeccodeId = userSeccodeId,
                    UserCode = userCode,
                    CanRead = true,
                    CanWrite = true,
                    CreatedBy = actor,
                    CreatedOn = now,
                });

                // SupplierUserMap + SecRight against supplier's type-G seccode (canWrite=false initially)
                var supplierSecRightId = Guid.NewGuid();
                _db.SecRights.Add(new SecRight
                {
                    Id = supplierSecRightId,
                    SeccodeId = supplier.SeccodeId,
                    UserCode = userCode,
                    CanRead = true,
                    CanWrite = false,
                    CreatedBy = actor,
                    CreatedOn = now,
                });
                _db.SupplierUserMaps.Add(new SupplierUserMap
                {
                    Id = Guid.NewGuid(),
                    SupplierId = supplier.Id,
                    AppUserId = userId,
                    SecRightId = supplierSecRightId,
                    CreatedBy = actor,
                    CreatedOn = now,
                });
            }
            else
            {
                _logger.LogInformation(
                    "Skipping auto-user provision for supplier {SupplierCode}: an AppUser already exists for {Email}.",
                    supplier.SupplierCode, contactEmail);
            }
        }
        else
        {
            _logger.LogWarning(
                "Supplier {SupplierCode} approved without any contacts; no portal user provisioned.",
                supplier.SupplierCode);
        }

        await _db.SaveChangesAsync(ct);

        // Send welcome email AFTER persistence so we never email an OTP for an aborted txn.
        if (newUser != null && oneTimePassword != null)
        {
            var baseUrl = _config["Web:BaseUrl"];
            var loginUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:5114" : baseUrl.TrimEnd('/');
            try
            {
                await _email.SendWelcomeEmailAsync(
                    newUser.Email,
                    newUser.FullName,
                    newUser.UserCode,
                    oneTimePassword,
                    loginUrl,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Welcome email send failed for {UserCode} ({Email}). User was created successfully.",
                    newUser.UserCode, newUser.Email);
            }
        }

        return Unit.Value;
    }

    private async Task<string> ResolveUniqueUserCodeAsync(string baseCode, CancellationToken ct)
    {
        var existing = await _db.AppUsers.IgnoreQueryFilters()
            .Where(u => u.UserCode == baseCode || u.UserCode.StartsWith(baseCode + "-"))
            .Select(u => u.UserCode)
            .ToListAsync(ct);
        if (!existing.Contains(baseCode, StringComparer.OrdinalIgnoreCase)) return baseCode;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseCode}-{i}";
            if (!existing.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
        // Should never happen in practice.
        return $"{baseCode}-{Guid.NewGuid():N}".Substring(0, 50);
    }
}
