namespace MerinoOne.SupplierPortal.Contracts.Integration;

/// <summary>
/// Create an inbound X-APIKey credential. Bound to one or more source/shared companies (Enhancement
/// Round 2 / Feature C — Infor LN needs a single key usable across several companies) and a set of
/// endpoint scopes (e.g. "Integration.Inbound.PaymentTerm"). The plaintext key is returned ONCE.
/// </summary>
public record CreateApiKeyRequest(
    string Label,
    IReadOnlyList<Guid> CompanyIds,
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
    IReadOnlyList<Guid> CompanyIds,
    IReadOnlyList<string> CompanyCodes,
    IReadOnlyList<string> Scopes,
    DateTime? ExpiresAt);

/// <summary>Safe projection — never carries the hash or plaintext.</summary>
public record ApiKeyDto(
    Guid Id,
    int Seq,
    string Label,
    string KeyPrefix,
    IReadOnlyList<Guid> CompanyIds,
    IReadOnlyList<string> CompanyCodes,
    IReadOnlyList<string> Scopes,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    DateTime? RevokedAt,
    bool IsActive,
    Guid? ReplacedByApiKeyId,
    DateTime CreatedOn);
