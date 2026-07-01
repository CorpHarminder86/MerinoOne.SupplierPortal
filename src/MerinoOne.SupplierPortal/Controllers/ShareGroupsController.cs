using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.ShareGroups;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Tenant-Admin CRUD for endpoint-wise table-sharing groups (CompanyShareGroup + members). For a given
/// master endpoint (Payment Term / Delivery Term) the member companies all read/write a single shared
/// dataset stored under the source company. Reads require <c>Integration.Read</c>; writes require
/// <c>Integration.Manage</c>. All operations are scoped to the caller's tenant.
/// </summary>
[ApiController]
[Authorize]
[Route("api/integration/share-groups")]
public class ShareGroupsController : ControllerBase
{
    private readonly IMediator _mediator;
    public ShareGroupsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.IntegrationRead)]
    [EndpointSummary("List share groups")]
    [EndpointDescription(@"Lists every endpoint-wise table-sharing group in the caller's tenant, each with its members resolved to company code + name.
Filters / params:
- **endpoint**: Optional — filter to a single SharedEndpoint (""PaymentTerm"" / ""DeliveryTerm""). An unknown value returns an empty list.
Returns: List<ShareGroupDto> scoped to the caller's tenant. Requires permission **Integration.Read**.")]
    public async Task<Result<List<ShareGroupDto>>> List([FromQuery] string? endpoint, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetShareGroupsQuery(endpoint), ct);
        return Result<List<ShareGroupDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = Perm.IntegrationManage)]
    [EndpointSummary("Create share group")]
    [EndpointDescription(@"Creates a share group (endpoint + source company + initial members) for the caller's tenant.
Body:
- **body**: CreateShareGroupRequest — Endpoint (SharedEndpoint name), SourceTenantEntityId, Name, MemberTenantEntityIds.
Validation:
- Endpoint must parse to a SharedEndpoint; Name non-empty; source + every member must be a company in the tenant.
- 409 if a group already exists for (endpoint, source), or if any member is already in another group for the same endpoint.
Returns: the new group's GUID; 400 on validation; 404 if a company is not in the tenant; 409 on a uniqueness conflict. Requires permission **Integration.Manage**.")]
    public async Task<Result<Guid>> Create([FromBody] CreateShareGroupRequest body, CancellationToken ct)
    {
        var id = await _mediator.Send(new CreateShareGroupCommand(body), ct);
        return Result<Guid>.Ok(id, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Perm.IntegrationManage)]
    [EndpointSummary("Update share group")]
    [EndpointDescription(@"Updates a share group's display name + enabled flag (endpoint / source / members are not editable here).
Filters / params:
- **id**: Required — share group GUID.
Body:
- **body**: UpdateShareGroupRequest with the new Name and IsEnabled flag.
Returns: empty success; 404 if not found in the tenant; 400 on validation. Requires permission **Integration.Manage**.")]
    public async Task<Result> Update(Guid id, [FromBody] UpdateShareGroupRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateShareGroupCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/members")]
    [Authorize(Policy = Perm.IntegrationManage)]
    [EndpointSummary("Add share group member")]
    [EndpointDescription(@"Adds a company to a share group as a member. A previously removed (soft-deleted) membership is restored rather than duplicated.
Filters / params:
- **id**: Required — share group GUID.
Body:
- **body**: AddShareGroupMemberRequest with the company's TenantEntityId.
Validation:
- The company must be in the tenant and not already in another group for the same endpoint.
Returns: empty success; 404 if the group / company is not in the tenant; 409 if the company is already mapped on this endpoint. Requires permission **Integration.Manage**.")]
    public async Task<Result> AddMember(Guid id, [FromBody] AddShareGroupMemberRequest body, CancellationToken ct)
    {
        await _mediator.Send(new AddShareGroupMemberCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}/members/{tenantEntityId:guid}")]
    [Authorize(Policy = Perm.IntegrationManage)]
    [EndpointSummary("Remove share group member")]
    [EndpointDescription(@"Soft-deletes a single member of a share group (by the member's company id). The company is then free to join another group on this endpoint.
Filters / params:
- **id**: Required — share group GUID.
- **tenantEntityId**: Required — the member company's GUID.
Returns: empty success (idempotent — removing an already-gone member is a no-op); 404 if the group is not in the tenant. Requires permission **Integration.Manage**.")]
    public async Task<Result> RemoveMember(Guid id, Guid tenantEntityId, CancellationToken ct)
    {
        await _mediator.Send(new RemoveShareGroupMemberCommand(id, tenantEntityId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Perm.IntegrationManage)]
    [EndpointSummary("Delete share group")]
    [EndpointDescription(@"Soft-deletes a share group and all of its members. The member companies' master data falls back to per-company storage.
Filters / params:
- **id**: Required — share group GUID.
Returns: empty success; 404 if not found in the tenant. Requires permission **Integration.Manage**.")]
    public async Task<Result> Delete(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeleteShareGroupCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
