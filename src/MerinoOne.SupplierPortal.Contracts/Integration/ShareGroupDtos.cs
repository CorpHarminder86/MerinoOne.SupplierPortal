namespace MerinoOne.SupplierPortal.Contracts.Integration;

/// <summary>A member company of a <see cref="ShareGroupDto"/>, resolved to its company code + name.</summary>
public record ShareGroupMemberDto(
    Guid Id,
    Guid MemberTenantEntityId,
    string MemberCode,
    string MemberName);

/// <summary>
/// An endpoint-wise table-sharing group with its members resolved to code + name. For the given
/// <see cref="Endpoint"/> the member companies all read/write a single shared dataset stored under
/// the source company (<see cref="SourceTenantEntityId"/>).
/// </summary>
public record ShareGroupDto(
    Guid Id,
    string Endpoint,
    Guid SourceTenantEntityId,
    string SourceCode,
    string SourceName,
    string Name,
    bool IsEnabled,
    IReadOnlyList<ShareGroupMemberDto> Members);

/// <summary>
/// Create a share group for an endpoint + source company, with an initial member set. <see cref="Endpoint"/>
/// must parse to a SharedEndpoint name; every id must resolve to a company in the caller's tenant.
/// </summary>
public record CreateShareGroupRequest(
    string Endpoint,
    Guid SourceTenantEntityId,
    string Name,
    IReadOnlyList<Guid> MemberTenantEntityIds);

/// <summary>Update a share group's display name + enabled flag (endpoint/source/members are not editable here).</summary>
public record UpdateShareGroupRequest(
    string Name,
    bool IsEnabled);

/// <summary>Add a single company to a share group as a member.</summary>
public record AddShareGroupMemberRequest(
    Guid TenantEntityId);
