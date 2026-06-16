using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Integration.ApiKeys;

/// <summary>
/// Tenant-Admin: rotate a key. Mints a SUCCESSOR row (fresh plaintext, same tenant/company/scopes/expiry)
/// and revokes the predecessor, linking them via <see cref="ApiKey.ReplacedByApiKeyId"/>. The new
/// plaintext is returned ONCE. Both writes commit in a single SaveChanges (implicit transaction).
/// </summary>
public record RotateApiKeyCommand(Guid Id) : IRequest<ApiKeySecretDto>;

public class RotateApiKeyCommandHandler : IRequestHandler<RotateApiKeyCommand, ApiKeySecretDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IApiKeyHasher _hasher;

    public RotateApiKeyCommandHandler(IAppDbContext db, ICurrentUser user, IApiKeyHasher hasher)
    {
        _db = db;
        _user = user;
        _hasher = hasher;
    }

    public async Task<ApiKeySecretDto> Handle(RotateApiKeyCommand request, CancellationToken ct)
    {
        // Tenant-filtered (ApiKey is ITenantOwned) — a Tenant Admin can only rotate its own tenant's keys.
        var predecessor = await _db.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == request.Id, ct)
            ?? throw new NotFoundException("ApiKey", request.Id);

        var now = DateTime.UtcNow;
        var actor = string.IsNullOrEmpty(_user.UserCode) ? "api" : _user.UserCode;

        var scopes = ApiKeyScopes.Normalize(
            predecessor.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var (plaintext, prefix) = ApiKeyGenerator.Generate();
        var successorId = Guid.NewGuid();

        _db.ApiKeys.Add(new ApiKey
        {
            Id = successorId,
            TenantId = predecessor.TenantId,
            Label = predecessor.Label,
            KeyPrefix = prefix,
            KeyHash = _hasher.Hash(plaintext),
            Scopes = string.Join(",", scopes),
            TenantEntityId = predecessor.TenantEntityId,
            ExpiresAt = predecessor.ExpiresAt,
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        // Revoke the predecessor and link it to its successor.
        predecessor.IsActive = false;
        predecessor.RevokedAt = now;
        predecessor.ReplacedByApiKeyId = successorId;
        predecessor.UpdatedBy = actor;
        predecessor.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);

        return new ApiKeySecretDto(successorId, predecessor.Label, prefix, plaintext, predecessor.TenantEntityId, scopes, predecessor.ExpiresAt);
    }
}
