using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Roles.Commands;
using MerinoOne.SupplierPortal.Application.Roles.Queries;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/roles")]
public class RolesController : ControllerBase
{
    private readonly IMediator _mediator;
    public RolesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Role.Read")]
    public async Task<Result<List<RoleListItemDto>>> List(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetRoleListQuery(), ct);
        return Result<List<RoleListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Role.Read")]
    public async Task<Result<RoleDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetRoleByIdQuery(id), ct);
        return Result<RoleDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "Role.Write")]
    public async Task<Result<Guid>> Create([FromBody] CreateRoleRequest body, CancellationToken ct)
    {
        var id = await _mediator.Send(new CreateRoleCommand(body), ct);
        return Result<Guid>.Ok(id, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Role.Write")]
    public async Task<Result> Update(Guid id, [FromBody] UpdateRoleRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateRoleCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/permissions")]
    [Authorize(Policy = "Role.Write")]
    public async Task<Result> AssignPermissions(Guid id, [FromBody] AssignPermissionsRequest body, CancellationToken ct)
    {
        await _mediator.Send(new AssignPermissionsCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
