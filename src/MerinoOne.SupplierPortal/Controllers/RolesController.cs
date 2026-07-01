using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Roles.Commands;
using MerinoOne.SupplierPortal.Application.Roles.Queries;
using MerinoOne.SupplierPortal.Contracts.Users;
using RolesPaged = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Users.RoleListItemDto>;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/roles")]
public class RolesController : ControllerBase
{
    private readonly IMediator _mediator;
    public RolesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.RoleRead)]
    [EndpointSummary("Role list")]
    [EndpointDescription(@"Lists RBAC roles (paged) with per-role permission + user counts.
Filters / params:
- **page**: Optional — 1-based page number (default 1).
- **pageSize**: Optional — rows per page (default 50, max 200).
Returns: PagedResult<RoleListItemDto> ordered by name. Requires permission **Role.Read**.")]
    public async Task<Result<RolesPaged>> List(
        [FromQuery] int page, [FromQuery] int pageSize, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetRoleListQuery(page < 1 ? 1 : page, pageSize < 1 ? 50 : pageSize), ct);
        return Result<RolesPaged>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Perm.RoleRead)]
    [EndpointSummary("Role detail")]
    [EndpointDescription(@"Returns a single role with its current permission assignments.
Filters / params:
- **id**: Required — role GUID.
Returns: RoleDetailDto on success; 404 if not found. Requires permission **Role.Read**.")]
    public async Task<Result<RoleDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetRoleByIdQuery(id), ct);
        return Result<RoleDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = Perm.RoleWrite)]
    [EndpointSummary("Create role")]
    [EndpointDescription(@"Creates a new RBAC role.
Body:
- **body**: CreateRoleRequest with role name, description, and optional initial permissions.
Returns: GUID of the newly created role; 400 on validation; 409 if name already exists. Requires permission **Role.Write**.")]
    public async Task<Result<Guid>> Create([FromBody] CreateRoleRequest body, CancellationToken ct)
    {
        var id = await _mediator.Send(new CreateRoleCommand(body), ct);
        return Result<Guid>.Ok(id, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Perm.RoleWrite)]
    [EndpointSummary("Update role")]
    [EndpointDescription(@"Updates name / description of an existing role.
Filters / params:
- **id**: Required — role GUID.
Body:
- **body**: UpdateRoleRequest with updated metadata.
Returns: empty success; 404 if not found; 400 on validation. Requires permission **Role.Write**.")]
    public async Task<Result> Update(Guid id, [FromBody] UpdateRoleRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateRoleCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/permissions")]
    [Authorize(Policy = Perm.RoleWrite)]
    [EndpointSummary("Assign role permissions")]
    [EndpointDescription(@"Replaces the role's permission set with the supplied list.
Filters / params:
- **id**: Required — role GUID.
Body:
- **body**: AssignPermissionsRequest containing the full list of permission codes to assign.
Side effects:
- All previously assigned permissions not in the list are revoked.
- All users holding this role gain/lose the permissions on their NEXT request (resolved live server-side; no relogin).
Returns: empty success; 404 if role not found. Requires permission **Role.Write**.")]
    public async Task<Result> AssignPermissions(Guid id, [FromBody] AssignPermissionsRequest body, CancellationToken ct)
    {
        await _mediator.Send(new AssignPermissionsCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
