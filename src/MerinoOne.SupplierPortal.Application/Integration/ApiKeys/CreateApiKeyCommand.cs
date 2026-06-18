using System.Security.Cryptography;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Integration.ApiKeys;

/// <summary>
/// Tenant-Admin: mint an inbound X-APIKey credential. Generates <c>mok_</c> + base64url(32 random bytes),
/// stores only the short non-secret prefix + SHA-256 hash, and returns the plaintext ONCE. The key is
/// bound to a source/shared company (TenantEntity) and a set of endpoint scopes.
/// </summary>
public record CreateApiKeyCommand(CreateApiKeyRequest Body) : IRequest<ApiKeySecretDto>;

public class CreateApiKeyCommandValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyCommandValidator()
    {
        RuleFor(x => x.Body.Label).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body.CompanyIds).NotEmpty().WithMessage("At least one company is required.");
        RuleFor(x => x.Body.Scopes).NotEmpty().WithMessage("At least one scope is required.");
        RuleForEach(x => x.Body.Scopes)
            .Must(s => ApiKeyScopes.Allowed.Contains(s))
            .WithMessage(s => $"Unknown scope. Allowed: {string.Join(", ", ApiKeyScopes.Allowed)}.");
    }
}

public class CreateApiKeyCommandHandler : IRequestHandler<CreateApiKeyCommand, ApiKeySecretDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IApiKeyHasher _hasher;

    public CreateApiKeyCommandHandler(IAppDbContext db, ICurrentUser user, IApiKeyHasher hasher)
    {
        _db = db;
        _user = user;
        _hasher = hasher;
    }

    public async Task<ApiKeySecretDto> Handle(CreateApiKeyCommand request, CancellationToken ct)
    {
        var body = request.Body;
        var scopes = ApiKeyScopes.Normalize(body.Scopes);
        if (scopes.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["scopes"] = new[] { "At least one valid scope is required." }
            });

        var companyIds = (body.CompanyIds ?? Array.Empty<Guid>()).Distinct().ToList();
        if (companyIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["companyIds"] = new[] { "At least one company is required." }
            });

        var tenantId = _user.TenantId
            ?? throw new ConflictException("The current session has no tenant context.");

        // Every bound company must exist in the acting tenant. TenantEntity is tenant-filtered, so a
        // cross-tenant / non-existent id simply won't be loaded — surface the first missing one.
        var companies = await _db.TenantEntities
            .Where(e => companyIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Code })
            .ToListAsync(ct);

        var foundIds = companies.Select(c => c.Id).ToHashSet();
        var missing = companyIds.FirstOrDefault(id => !foundIds.Contains(id));
        if (missing != Guid.Empty || companies.Count != companyIds.Count)
            throw new NotFoundException("Company", missing == Guid.Empty ? companyIds.First() : missing);

        var (plaintext, prefix) = ApiKeyGenerator.Generate();
        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var id = Guid.NewGuid();

        // Multi-company binding (Feature C): one ApiKeyCompany junction row per bound company. The legacy
        // ApiKey.TenantEntityId is no longer set — readers resolve the binding from the junction.
        _db.ApiKeys.Add(new ApiKey
        {
            Id = id,
            TenantId = tenantId,
            Label = body.Label.Trim(),
            KeyPrefix = prefix,
            KeyHash = _hasher.Hash(plaintext),
            Scopes = string.Join(",", scopes),
            ExpiresAt = body.ExpiresAt,
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        foreach (var companyId in companyIds)
        {
            _db.ApiKeyCompanies.Add(new ApiKeyCompany
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ApiKeyId = id,
                TenantEntityId = companyId,
                CreatedBy = actor,
                CreatedOn = now
            });
        }

        await _db.SaveChangesAsync(ct);

        // Order codes by the company list for a stable response.
        var codeMap = companies.ToDictionary(c => c.Id, c => c.Code);
        var orderedCodes = companyIds.Select(cid => codeMap[cid]).ToList();

        return new ApiKeySecretDto(id, body.Label.Trim(), prefix, plaintext, companyIds, orderedCodes, scopes, body.ExpiresAt);
    }
}

/// <summary>
/// Canonical inbound scope tokens. Each maps to a permission claim minted by the API-key auth handler and
/// is enforced by the existing PermissionPolicyProvider via [Authorize(Policy="Integration.Inbound.*")].
/// </summary>
public static class ApiKeyScopes
{
    public static readonly IReadOnlyList<string> Allowed = new[]
    {
        "Integration.Inbound." + nameof(SharedEndpoint.PaymentTerm),
        "Integration.Inbound." + nameof(SharedEndpoint.DeliveryTerm),
        "Integration.Inbound." + nameof(SharedEndpoint.Unit),
        "Integration.Inbound." + nameof(SharedEndpoint.ItemGroup),
        "Integration.Inbound." + nameof(SharedEndpoint.Item),
        "Integration.Inbound." + nameof(TenantInboundEntity.Currency),
        "Integration.Inbound." + nameof(TenantInboundEntity.Country),
        "Integration.Inbound." + nameof(TenantInboundEntity.State),
        "Integration.Inbound." + nameof(TenantInboundEntity.City),
        "Integration.Inbound." + nameof(TenantInboundEntity.PostalCode),
    };

    public static List<string> Normalize(IEnumerable<string>? scopes) =>
        (scopes ?? Enumerable.Empty<string>())
            .Select(s => s?.Trim() ?? string.Empty)
            .Where(s => Allowed.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// Inbound key generation. <c>mok_</c> + base64url(32 bytes), mirroring CreateSupplierInviteCommand's
/// GenerateUrlSafeToken. The stored prefix is the first <see cref="PrefixLength"/> chars — keep this in
/// lock-step with ApiKeyAuthenticationHandler.PrefixLength in the API host.
/// </summary>
public static class ApiKeyGenerator
{
    public const string Prefix = "mok_";

    /// <summary>Leading chars stored as the non-secret lookup prefix (must equal the auth handler's value).</summary>
    public const int PrefixLength = 12;

    public static (string Plaintext, string KeyPrefix) Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var body = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var plaintext = Prefix + body;
        var keyPrefix = plaintext.Length >= PrefixLength ? plaintext[..PrefixLength] : plaintext;
        return (plaintext, keyPrefix);
    }
}
