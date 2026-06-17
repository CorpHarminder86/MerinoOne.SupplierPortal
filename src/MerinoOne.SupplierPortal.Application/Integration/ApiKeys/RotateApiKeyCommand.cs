using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Integration.ApiKeys;

/// <summary>
/// Tenant-Admin: rotate a key. Mints a SUCCESSOR row (fresh plaintext, same tenant/companies/scopes/expiry)
/// and revokes the predecessor, linking them via <see cref="ApiKey.ReplacedByApiKeyId"/>. The predecessor's
/// bound-company set (the <see cref="ApiKeyCompany"/> junction) is copied to the successor. The new
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

        // Copy the predecessor's bound-company set (Feature C). Resolve codes for the response too.
        var boundCompanies = await (
            from c in _db.ApiKeyCompanies
            where c.ApiKeyId == predecessor.Id
            join te in _db.TenantEntities on c.TenantEntityId equals te.Id
            select new { c.TenantEntityId, te.Code })
            .ToListAsync(ct);

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
            ExpiresAt = predecessor.ExpiresAt,
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = now
        });

        foreach (var bc in boundCompanies)
        {
            _db.ApiKeyCompanies.Add(new ApiKeyCompany
            {
                Id = Guid.NewGuid(),
                TenantId = predecessor.TenantId,
                ApiKeyId = successorId,
                TenantEntityId = bc.TenantEntityId,
                CreatedBy = actor,
                CreatedOn = now
            });
        }

        // Revoke the predecessor and link it to its successor.
        predecessor.IsActive = false;
        predecessor.RevokedAt = now;
        predecessor.ReplacedByApiKeyId = successorId;
        predecessor.UpdatedBy = actor;
        predecessor.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);

        var companyIds = boundCompanies.Select(b => b.TenantEntityId).ToList();
        var companyCodes = boundCompanies.Select(b => b.Code).ToList();

        return new ApiKeySecretDto(successorId, predecessor.Label, prefix, plaintext,
            companyIds, companyCodes, scopes, predecessor.ExpiresAt);
    }
}
