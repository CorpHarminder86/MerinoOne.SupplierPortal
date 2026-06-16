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
        RuleFor(x => x.Body.TenantEntityId).NotEmpty();
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

        // The bound source company must belong to the acting tenant. TenantEntity is tenant-filtered, so
        // a non-existent / cross-tenant id simply won't be found.
        var company = await _db.TenantEntities
            .FirstOrDefaultAsync(e => e.Id == body.TenantEntityId, ct)
            ?? throw new NotFoundException("Company", body.TenantEntityId);

        var tenantId = _user.TenantId
            ?? throw new ConflictException("The current session has no tenant context.");

        var (plaintext, prefix) = ApiKeyGenerator.Generate();
        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;
        var id = Guid.NewGuid();

        _db.ApiKeys.Add(new ApiKey
        {
            Id = id,
            TenantId = tenantId,
            Label = body.Label.Trim(),
            KeyPrefix = prefix,
            KeyHash = _hasher.Hash(plaintext),
            Scopes = string.Join(",", scopes),
            TenantEntityId = company.Id,
            ExpiresAt = body.ExpiresAt,
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        await _db.SaveChangesAsync(ct);

        return new ApiKeySecretDto(id, body.Label.Trim(), prefix, plaintext, company.Id, scopes, body.ExpiresAt);
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
