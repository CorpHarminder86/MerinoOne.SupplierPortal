using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Permissions.Commands;
using MerinoOne.SupplierPortal.Application.Permissions.Queries;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/permissions")]
public class PermissionsController : ControllerBase
{
    private readonly IMediator _mediator;
    public PermissionsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.RoleRead)]
    [EndpointSummary("Permission catalog")]
    [EndpointDescription(@"Lists the human-assignable permissions, projected from the seeded catalog so the
role-permission picker can never drift from the backend. Service-to-service (Integration.Inbound.*) and
platform-tier (Platform.*) scopes are excluded.
Returns: List<PermissionListItemDto> ordered by module then code. Requires permission **Role.Read**.")]
    public async Task<Result<List<PermissionListItemDto>>> List(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPermissionListQuery(), ct);
        return Result<List<PermissionListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = Perm.PlatformPermissions)]
    [EndpointSummary("Create permission")]
    [EndpointDescription(@"Registers a new GLOBAL permission code in the catalog. Platform-operator only.
NOTE: a permission code enforces nothing until a matching [Authorize(Policy = ...)] gate references it in code.
Body:
- **body**: CreatePermissionRequest (Code dotted PascalCase, Name, optional Module/Description).
Returns: the created permission code; 400 on validation; 409 if the code already exists. Requires permission **Platform.Permissions**.")]
    public async Task<Result<string>> Create([FromBody] CreatePermissionRequest body, CancellationToken ct)
    {
        var code = await _mediator.Send(new CreatePermissionCommand(body), ct);
        return Result<string>.Ok(code, HttpContext.TraceIdentifier);
    }
}
