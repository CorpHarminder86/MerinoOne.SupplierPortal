using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// X-APIKey credential for inbound integration (consumed by Infor LN). Tenant-scoped, bound to a
/// source/shared company (<see cref="TenantEntityId"/>) and a set of endpoint scopes. Only the
/// SHA-256 <see cref="KeyHash"/> + the short <see cref="KeyPrefix"/> are stored; the plaintext key is
/// returned once at creation. Backend owns generation/verification (see ApiKeyHasher, deferred).
/// </summary>
public class ApiKey : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Short non-secret prefix used for O(1) lookup before the constant-time hash compare.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>SHA-256 hex digest (char(64)) of the full plaintext key. Never reversible.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Comma/space-delimited endpoint scopes, e.g. "Integration.Inbound.PaymentTerm".</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>
    /// Bound source company (legacy single-company binding). TRANSITIONAL: superseded by the multi-company
    /// <see cref="Companies"/> junction (Enhancement Round 2, Feature C). Kept + still mapped so existing
    /// readers compile while backend-developer migrates them to the junction; a follow-up migration (_0014)
    /// drops this column once those readers are off it. Migration _0013 backfills one junction row per key
    /// from this value. Do not add NEW readers of this property.
    /// </summary>
    public Guid? TenantEntityId { get; set; }
    public Admin.TenantEntity? TenantEntity { get; set; }

    /// <summary>Companies this key is bound to. A key may bind several (multi-company keys, Feature C).</summary>
    public ICollection<ApiKeyCompany> Companies { get; set; } = new List<ApiKeyCompany>();

    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Set by RotateApiKeyCommand — points to the successor key after rotation (deferred).</summary>
    public Guid? ReplacedByApiKeyId { get; set; }
}
