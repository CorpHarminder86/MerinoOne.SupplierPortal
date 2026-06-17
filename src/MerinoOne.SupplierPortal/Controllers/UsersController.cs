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
    [EndpointSummary("User list")]
    [EndpointDescription(@"Lists application users matching the supplied filters.
Filters / params:
- **search**: Optional — free-text on user code / name / email.
- **isInternal**: Optional — true = internal buyer users, false = supplier users.
- **isActive**: Optional — true = active only, false = inactive only.
Returns: List<UserListItemDto>. Requires permission **User.Read**.")]
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
    [EndpointSummary("User detail")]
    [EndpointDescription(@"Full user profile + assigned roles + supplier mappings.
Filters / params:
- **id**: Required — user GUID.
Returns: UserDetailDto on success; 404 if not found. Requires permission **User.Read**.")]
    public async Task<Result<UserDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetUserByIdQuery(id), ct);
        return Result<UserDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "User.Write")]
    [EndpointSummary("Create user")]
    [EndpointDescription(@"Provisions a new application user.
Body:
- **body**: CreateUserRequest with user code, contact details, initial password, internal/external flag.
Side effects:
- Persists the user in active state with MustChangePassword = true.
Returns: GUID of the newly created user; 400 on validation; 409 if user code already in use. Requires permission **User.Write**.")]
    public async Task<Result<Guid>> Create([FromBody] CreateUserRequest body, CancellationToken ct)
    {
        var id = await _mediator.Send(new CreateUserCommand(body), ct);
        return Result<Guid>.Ok(id, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "User.Write")]
    [EndpointSummary("Update user")]
    [EndpointDescription(@"Updates profile fields on an existing user.
Filters / params:
- **id**: Required — user GUID.
Body:
- **body**: UpdateUserRequest with updated metadata (name, email, MFA flag, etc.).
Returns: empty success; 404 if not found; 400 on validation. Requires permission **User.Write**.")]
    public async Task<Result> Update(Guid id, [FromBody] UpdateUserRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateUserCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/roles")]
    [Authorize(Policy = "User.Write")]
    [EndpointSummary("Assign role")]
    [EndpointDescription(@"Grants one role to a user.
Filters / params:
- **id**: Required — user GUID.
Body:
- **body**: AssignRoleRequest with the role GUID to assign.
Side effects:
- The user gains the role's permissions on next JWT refresh.
Returns: empty success; 404 if user or role not found; 409 if already assigned. Requires permission **User.Write**.")]
    public async Task<Result> AssignRole(Guid id, [FromBody] AssignRoleRequest body, CancellationToken ct)
    {
        await _mediator.Send(new AssignRoleCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}/roles/{roleId:guid}")]
    [Authorize(Policy = "User.Write")]
    [EndpointSummary("Remove role")]
    [EndpointDescription(@"Revokes a role from a user.
Filters / params:
- **id**: Required — user GUID.
- **roleId**: Required — role GUID to revoke.
Side effects:
- Soft-deletes the user-role mapping; permissions disappear on next JWT refresh.
Returns: empty success; 404 if mapping not found. Requires permission **User.Write**.")]
    public async Task<Result> RemoveRole(Guid id, Guid roleId, CancellationToken ct)
    {
        await _mediator.Send(new RemoveRoleCommand(id, roleId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/supplier-maps")]
    [Authorize(Policy = "Supplier.Provision")]
    [EndpointSummary("Map user to supplier")]
    [EndpointDescription(@"Links a supplier user to one or more suppliers (drives seccode-scoped data visibility).
Filters / params:
- **id**: Required — user GUID.
Body:
- **body**: MapSupplierRequest with the supplier GUID.
Side effects:
- Creates the SupplierUserMap row; the user immediately sees data for that supplier on next request.
Returns: empty success; 404 if user or supplier not found. Requires permission **Supplier.Provision**.")]
    public async Task<Result> MapSupplier(Guid id, [FromBody] MapSupplierRequest body, CancellationToken ct)
    {
        await _mediator.Send(new MapSupplierCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}/supplier-maps/{supplierId:guid}")]
    [Authorize(Policy = "Supplier.Provision")]
    [EndpointSummary("Unmap user from supplier")]
    [EndpointDescription(@"Removes a user's mapping to a supplier (revokes seccode-scoped access).
Filters / params:
- **id**: Required — user GUID.
- **supplierId**: Required — supplier GUID to unmap.
Side effects:
- Soft-deletes the SupplierUserMap row.
Returns: empty success; 404 if mapping not found. Requires permission **Supplier.Provision**.")]
    public async Task<Result> UnmapSupplier(Guid id, Guid supplierId, CancellationToken ct)
    {
        await _mediator.Send(new UnmapSupplierCommand(id, supplierId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}/supplier-maps")]
    [Authorize(Policy = "Supplier.Provision")]
    [EndpointSummary("Bulk set user supplier maps for a company")]
    [EndpointDescription(@"Reconciles a user's supplier maps for ONE company as a diff (Enhancement Round 2 / Feature B).
Filters / params:
- **id**: Required — user GUID.
- **tenantEntityId**: Required (query) — the company the supplier set belongs to.
Body:
- **body**: SetSupplierMapsRequest with the desired supplier GUIDs + a single CanWrite flag for the batch.
Side effects:
- Adds missing maps, removes extras, and updates CanWrite on kept rows — all in one transaction. An empty SupplierIds list removes every map for THIS company only. Auto-creates the supplier-derived UserCompanyMap if absent.
Returns: empty success; 404 if user/company/supplier not found; 409 if a supplier belongs to a different company. Requires permission **Supplier.Provision**.")]
    public async Task<Result> SetSupplierMaps(Guid id, [FromQuery] Guid tenantEntityId,
        [FromBody] SetSupplierMapsRequest body, CancellationToken ct)
    {
        await _mediator.Send(new SetCompanySupplierMapsCommand(id, tenantEntityId, body.SupplierIds, body.CanWrite), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/company-maps")]
    [Authorize(Policy = "Supplier.Provision")]
    [EndpointSummary("Grant user full-access company")]
    [EndpointDescription(@"Grants a user DIRECT FULL access to a company — every supplier in it, incl. future ones (Enhancement Round 2 / Feature A).
Filters / params:
- **id**: Required — user GUID.
Body:
- **body**: GrantCompanyRequest with the company GUID.
Side effects:
- Creates a UserCompanyMap with AllSuppliers = true (seccode bypass scoped to that company); upgrades an existing supplier-derived map to full access; restores a soft-deleted map. The first company becomes the default active company.
Returns: empty success; 404 if user/company not found; 409 if the company belongs to a different tenant. Requires permission **Supplier.Provision**.")]
    public async Task<Result> GrantCompany(Guid id, [FromBody] GrantCompanyRequest body, CancellationToken ct)
    {
        await _mediator.Send(new GrantUserCompanyCommand(id, body.TenantEntityId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}/company-maps/{tenantEntityId:guid}")]
    [Authorize(Policy = "Supplier.Provision")]
    [EndpointSummary("Revoke user company access")]
    [EndpointDescription(@"Removes a user's access to one company (soft-deletes the UserCompanyMap). If the removed company was the user's default active company, the default is moved to another remaining company.
Filters / params:
- **id**: Required — user GUID.
- **tenantEntityId**: Required — company GUID to revoke.
Side effects:
- Soft-deletes the UserCompanyMap; the always-on company filter hides that company's data from the user on next request.
Returns: empty success; 404 if no active mapping. Requires permission **Supplier.Provision**.")]
    public async Task<Result> RemoveCompany(Guid id, Guid tenantEntityId, CancellationToken ct)
    {
        await _mediator.Send(new RemoveUserCompanyCommand(id, tenantEntityId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/password")]
    [Authorize(Policy = "User.Write")]
    [EndpointSummary("Set user password (admin)")]
    [EndpointDescription(@"Admin sets / resets another user's password.
Filters / params:
- **id**: Required — user GUID.
Body:
- **body**: SetPasswordRequest with the new password.
Side effects:
- Stores a fresh hash and sets MustChangePassword = true so the user changes it on next login.
Returns: empty success; 404 if user not found; 400 on policy violation. Requires permission **User.Write**.")]
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
    [EndpointSummary("Change own password")]
    [EndpointDescription(@"The current user changes their own password.
Body:
- **body**: ChangeOwnPasswordRequest with current + new password.
Side effects:
- Verifies the current password before persisting the new hash.
- Clears MustChangePassword so the user can proceed past the change-password gate.
Returns: empty success; 400 if current password mismatch or policy violation. Any authenticated user.")]
    public async Task<Result> ChangeOwnPassword([FromBody] ChangeOwnPasswordRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ChangeOwnPasswordCommand(body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = "User.Write")]
    [EndpointSummary("Deactivate user")]
    [EndpointDescription(@"Marks a user as inactive — blocks future logins without deleting history.
Filters / params:
- **id**: Required — user GUID.
Side effects:
- Sets IsActive = false; any existing JWT remains valid until expiry.
Returns: empty success; 404 if not found. Requires permission **User.Write**.")]
    public async Task<Result> Deactivate(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeactivateUserCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = "User.Write")]
    [EndpointSummary("Reactivate user")]
    [EndpointDescription(@"Restores a previously deactivated user.
Filters / params:
- **id**: Required — user GUID.
Side effects:
- Sets IsActive = true; the user can log in on next attempt.
Returns: empty success; 404 if not found. Requires permission **User.Write**.")]
    public async Task<Result> Reactivate(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new ReactivateUserCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
