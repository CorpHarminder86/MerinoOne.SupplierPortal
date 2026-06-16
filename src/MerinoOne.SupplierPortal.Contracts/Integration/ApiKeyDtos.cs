namespace MerinoOne.SupplierPortal.Contracts.Integration;

/// <summary>
/// Create an inbound X-APIKey credential. Bound to a source/shared company and a set of endpoint
/// scopes (e.g. "Integration.Inbound.PaymentTerm"). The plaintext key is returned ONCE.
/// </summary>
public record CreateApiKeyRequest(
    string Label,
    Guid TenantEntityId,
    IReadOnlyList<string> Scopes,
    DateTime? ExpiresAt);

/// <summary>
/// Returned ONCE at creation/rotation — carries the plaintext key. After this response the key is
/// unrecoverable (only its SHA-256 hash + prefix are stored).
/// </summary>
public record ApiKeySecretDto(
    Guid Id,
    string Label,
    string KeyPrefix,
    string PlaintextKey,
    Guid? TenantEntityId,
    IReadOnlyList<string> Scopes,
    DateTime? ExpiresAt);

/// <summary>Safe projection — never carries the hash or plaintext.</summary>
public record ApiKeyDto(
    Guid Id,
    int Seq,
    string Label,
    string KeyPrefix,
    Guid? TenantEntityId,
    string? CompanyCode,
    IReadOnlyList<string> Scopes,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt,
    bool IsActive,
    Guid? ReplacedByApiKeyId,
    DateTime CreatedOn);
