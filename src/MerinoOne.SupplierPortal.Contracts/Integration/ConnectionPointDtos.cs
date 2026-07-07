namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R10 — connection points (named outbound connection targets). One default per tenant; outbound
// integration configs tag a connection via connectionPointId (NULL = default).

/// <summary>List projection. <c>HasAuthConfig</c> masks the encrypted blob; <c>InUseCount</c> = live configs tagging this connection.</summary>
public sealed record ConnectionPointDto(
    Guid Id,
    int Seq,
    string Name,
    string SystemType,
    string? BaseUrl,
    bool IsDefault,
    string? Notes,
    bool HasAuthConfig,
    int InUseCount,
    bool TransportAvailable,
    DateTime CreatedOn,
    DateTime? UpdatedOn);

/// <summary>Create/update. <c>AuthConfigJson</c> null = keep stored value; InforION rows carry no URL/auth
/// (resolved from the tenant's Infor connection settings).</summary>
public sealed record SaveConnectionPointRequest(
    Guid? Id,
    string Name,
    string SystemType,
    string? BaseUrl,
    string? AuthConfigJson,
    string? Notes);
