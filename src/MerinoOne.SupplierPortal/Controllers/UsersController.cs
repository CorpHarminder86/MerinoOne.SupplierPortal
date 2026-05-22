using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Users.Commands;
using MerinoOne.SupplierPortal.Application.Users.Queries;
using MerinoOne.SupplierPortal.Contracts.Auth;
using MerinoOne.SupplierPortal.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly IMediator _mediator;
    public UsersController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "User.Read")]
    public async Task<Result<List<UserListItemDto>>> List(
        [FromQuery] string? search,
        [FromQuery] bool? isInternal,
        [FromQuery] bool? isActive,
        CancellationToken ct)
    {
        var data = await _mediator.Send(new GetUserListQuery(search, isInternal, isActive), ct);
        return Result<List<UserListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "User.Read")]
    public async Task<Result<UserDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetUserByIdQuery(id), ct);
        return Result<UserDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "User.Write")]
    public async Task<Result<Guid>> Create([FromBody] CreateUserRequest body, CancellationToken ct)
    {
        var id = await _mediator.Send(new CreateUserCommand(body), ct);
        return Result<Guid>.Ok(id, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "User.Write")]
    public async Task<Result> Update(Guid id, [FromBody] UpdateUserRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateUserCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/roles")]
    [Authorize(Policy = "User.Write")]
    public async Task<Result> AssignRole(Guid id, [FromBody] AssignRoleRequest body, CancellationToken ct)
    {
        await _mediator.Send(new AssignRoleCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}/roles/{roleId:guid}")]
    [Authorize(Policy = "User.Write")]
    public async Task<Result> RemoveRole(Guid id, Guid roleId, CancellationToken ct)
    {
        await _mediator.Send(new RemoveRoleCommand(id, roleId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/supplier-maps")]
    [Authorize(Policy = "Supplier.Provision")]
    public async Task<Result> MapSupplier(Guid id, [FromBody] MapSupplierRequest body, CancellationToken ct)
    {
        await _mediator.Send(new MapSupplierCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}/supplier-maps/{supplierId:guid}")]
    [Authorize(Policy = "Supplier.Provision")]
    public async Task<Result> UnmapSupplier(Guid id, Guid supplierId, CancellationToken ct)
    {
        await _mediator.Send(new UnmapSupplierCommand(id, supplierId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/password")]
    [Authorize(Policy = "User.Write")]
    public async Task<Result> SetPassword(Guid id, [FromBody] SetPasswordRequest body, CancellationToken ct)
    {
        await _mediator.Send(new SetPasswordCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    /// <summary>
    /// Any authenticated user may change their own password. Clears the forced-change
    /// flag so the user can proceed past the change-password gate.
    /// </summary>
    [HttpPost("me/change-password")]
    public async Task<Result> ChangeOwnPassword([FromBody] ChangeOwnPasswordRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ChangeOwnPasswordCommand(body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = "User.Write")]
    public async Task<Result> Deactivate(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivateUserCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = "User.Write")]
    public async Task<Result> Reactivate(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new ReactivateUserCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
