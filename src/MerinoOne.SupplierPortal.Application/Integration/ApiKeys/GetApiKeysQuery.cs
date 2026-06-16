using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.ApiKeys;

/// <summary>
/// Tenant-Admin: list the current tenant's API keys. NEVER returns the hash or plaintext — only the
/// non-secret prefix + metadata. Tenant-filtered (ApiKey is ITenantOwned). By default includes revoked
/// keys so the rotation history is visible; pass <c>activeOnly</c> to hide them.
/// </summary>
public record GetApiKeysQuery(bool ActiveOnly = false) : IRequest<List<ApiKeyDto>>;

public class GetApiKeysQueryHandler : IRequestHandler<GetApiKeysQuery, List<ApiKeyDto>>
{
    private readonly IAppDbContext _db;
    public GetApiKeysQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<ApiKeyDto>> Handle(GetApiKeysQuery request, CancellationToken ct)
    {
        var q = _db.ApiKeys.AsQueryable();
        if (request.ActiveOnly)
            q = q.Where(k => k.IsActive);

        var rows = await q
            .OrderByDescending(k => k.CreatedOn)
            .Select(k => new
            {
                k.Id,
                k.Seq,
                k.Label,
                k.KeyPrefix,
                k.TenantEntityId,
                CompanyCode = k.TenantEntity != null ? k.TenantEntity.Code : null,
                k.Scopes,
                k.ExpiresAt,
                k.LastUsedAt,
                k.RevokedAt,
                k.IsActive,
                k.ReplacedByApiKeyId,
                k.CreatedOn
            })
            .ToListAsync(ct);

        return rows.Select(k => new ApiKeyDto(
            k.Id, k.Seq, k.Label, k.KeyPrefix, k.TenantEntityId, k.CompanyCode,
            string.IsNullOrWhiteSpace(k.Scopes)
                ? Array.Empty<string>()
                : k.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            k.ExpiresAt, k.LastUsedAt, k.RevokedAt, k.IsActive, k.ReplacedByApiKeyId, k.CreatedOn))
            .ToList();
    }
}
