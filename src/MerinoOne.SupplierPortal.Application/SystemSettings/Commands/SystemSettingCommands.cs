using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;
using MerinoOne.SupplierPortal.Application.SystemSettings.Registry;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using MerinoOne.SupplierPortal.Domain.Entities.Settings;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.Commands;

// ─── Save ────────────────────────────────────────────────────────────────────
public record SaveSystemSettingCommand(SaveSystemSettingRequest Body) : IRequest<Unit>;

public class SaveSystemSettingCommandValidator : AbstractValidator<SaveSystemSettingCommand>
{
    public SaveSystemSettingCommandValidator()
    {
        RuleFor(x => x.Body.Category).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.Key).NotEmpty().MaximumLength(100);
        // SettingValue is nvarchar(max) in DB; cap at a sane ceiling.
        RuleFor(x => x.Body.Value).NotNull().MaximumLength(4000);
    }
}

public class SaveSystemSettingCommandHandler : IRequestHandler<SaveSystemSettingCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SettingsSeedRegistry _registry;
    private readonly ISettingProtector _protector;
    private readonly IEnumerable<ISettingsCacheInvalidator> _invalidators;

    public SaveSystemSettingCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        SettingsSeedRegistry registry,
        ISettingProtector protector,
        IEnumerable<ISettingsCacheInvalidator> invalidators)
    {
        _db = db;
        _user = user;
        _registry = registry;
        _protector = protector;
        _invalidators = invalidators;
    }

    public async Task<Unit> Handle(SaveSystemSettingCommand request, CancellationToken ct)
    {
        var (category, key, value) = (request.Body.Category, request.Body.Key, request.Body.Value ?? string.Empty);

        // Per-category semantic validation. If the registry knows the category, run its rule;
        // unknown categories pass through (caller's prerogative — keeps the shell generic).
        _registry.TryGet(category, out var seed);
        if (seed != null)
        {
            var error = seed.Validate(key, value);
            if (error != null)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    [nameof(request.Body.Value)] = new[] { error }
                });
            }
        }

        // Cross-field: an empty UserName is only acceptable when DefaultCredentials is on.
        if (string.Equals(category, EmailConfigKeys.Category, StringComparison.Ordinal)
            && string.Equals(key, EmailConfigKeys.UserName, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(value))
        {
            var siblingDefaultCreds = await _db.SystemSettings
                .Where(s => s.Category == EmailConfigKeys.Category
                            && s.SettingKey == EmailConfigKeys.DefaultCredentials
                            && s.IsActive)
                .Select(s => s.SettingValue)
                .FirstOrDefaultAsync(ct) ?? "true";

            if (bool.TryParse(siblingDefaultCreds, out var useDefault) && !useDefault)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    [nameof(request.Body.Value)] =
                        new[] { "UserName is required when DefaultCredentials is false." }
                });
            }
        }

        var row = await _db.SystemSettings
            .FirstOrDefaultAsync(s => s.Category == category && s.SettingKey == key, ct);

        // Resolve the value to persist. EmailConfig.Password takes the encryption fork; if the
        // caller echoed back our masked sentinel, leave the existing ciphertext untouched.
        string toPersist;
        var isPasswordKey = string.Equals(category, EmailConfigKeys.Category, StringComparison.Ordinal)
                            && string.Equals(key, EmailConfigKeys.Password, StringComparison.Ordinal);

        if (isPasswordKey)
        {
            if (string.Equals(value, EmailConfigKeys.PasswordMask, StringComparison.Ordinal))
            {
                // No-op — keep existing ciphertext. Fan out invalidation anyway in case the
                // caller is using this as a "force refresh" of a stale cache. Then bail out.
                Invalidate(category);
                return Unit.Value;
            }
            toPersist = string.IsNullOrEmpty(value) ? string.Empty : _protector.Protect(value);
        }
        else
        {
            toPersist = value;
        }

        var actor = string.IsNullOrEmpty(_user?.UserCode) ? "system" : _user.UserCode;
        var now = DateTime.UtcNow;

        if (row == null)
        {
            // Row missing — synthesise from the seed if we have one, otherwise raise.
            if (seed == null || !seed.Defaults.ContainsKey(key))
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    [nameof(request.Body.Key)] =
                        new[] { $"Unknown setting '{category}.{key}'." }
                });
            }
            seed.Descriptions.TryGetValue(key, out var desc);
            row = new SystemSetting
            {
                Id = Guid.NewGuid(),
                Category = category,
                SettingKey = key,
                SettingValue = toPersist,
                Description = desc,
                IsActive = true,
                CreatedBy = actor,
                CreatedOn = now,
            };
            _db.SystemSettings.Add(row);
        }
        else
        {
            row.SettingValue = toPersist;
            row.UpdatedBy = actor;
            row.UpdatedOn = now;
        }

        await _db.SaveChangesAsync(ct);
        Invalidate(category);
        return Unit.Value;
    }

    private void Invalidate(string category)
    {
        foreach (var inv in _invalidators) inv.InvalidateCategory(category);
    }
}

// ─── Reset ───────────────────────────────────────────────────────────────────
public record ResetSystemSettingCommand(ResetSystemSettingRequest Body) : IRequest<Unit>;

public class ResetSystemSettingCommandValidator : AbstractValidator<ResetSystemSettingCommand>
{
    public ResetSystemSettingCommandValidator()
    {
        RuleFor(x => x.Body.Category).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.Key).NotEmpty().MaximumLength(100);
    }
}

public class ResetSystemSettingCommandHandler : IRequestHandler<ResetSystemSettingCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly SettingsSeedRegistry _registry;
    private readonly ISettingProtector _protector;
    private readonly IEnumerable<ISettingsCacheInvalidator> _invalidators;

    public ResetSystemSettingCommandHandler(
        IAppDbContext db,
        ICurrentUser user,
        SettingsSeedRegistry registry,
        ISettingProtector protector,
        IEnumerable<ISettingsCacheInvalidator> invalidators)
    {
        _db = db;
        _user = user;
        _registry = registry;
        _protector = protector;
        _invalidators = invalidators;
    }

    public async Task<Unit> Handle(ResetSystemSettingCommand request, CancellationToken ct)
    {
        var (category, key) = (request.Body.Category, request.Body.Key);

        if (!_registry.TryGet(category, out var seed) || seed == null
            || !seed.Defaults.TryGetValue(key, out var defaultValue))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(request.Body.Key)] =
                    new[] { $"No seed default registered for '{category}.{key}'." }
            });
        }

        var actor = string.IsNullOrEmpty(_user?.UserCode) ? "system" : _user.UserCode;
        var now = DateTime.UtcNow;

        var row = await _db.SystemSettings
            .FirstOrDefaultAsync(s => s.Category == category && s.SettingKey == key, ct);

        if (row == null)
        {
            seed.Descriptions.TryGetValue(key, out var desc);
            row = new SystemSetting
            {
                Id = Guid.NewGuid(),
                Category = category,
                SettingKey = key,
                SettingValue = defaultValue,
                Description = desc,
                IsActive = true,
                CreatedBy = actor,
                CreatedOn = now,
            };
            _db.SystemSettings.Add(row);
        }
        else
        {
            row.SettingValue = defaultValue;
            row.UpdatedBy = actor;
            row.UpdatedOn = now;
        }

        await _db.SaveChangesAsync(ct);
        foreach (var inv in _invalidators) inv.InvalidateCategory(category);
        return Unit.Value;
    }
}
