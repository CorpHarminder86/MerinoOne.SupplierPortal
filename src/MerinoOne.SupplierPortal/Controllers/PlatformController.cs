using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Platform.Commands;
using MerinoOne.SupplierPortal.Application.Platform.Queries;
using MerinoOne.SupplierPortal.Contracts.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Platform-Admin onboarding surface (cross-tenant). Every action requires the Platform-only
/// "Platform.Onboard" permission; a Platform Admin bypasses the tenant filter and holds NO
/// business-data permissions (separation of duties). Thin — delegates to MediatR.
/// </summary>
[ApiController]
[Authorize(Policy = Perm.PlatformOnboard)]
[Route("api/platform")]
public class PlatformController : ControllerBase
{
    private readonly IMediator _mediator;
    public PlatformController(IMediator mediator) => _mediator = mediator;

    [HttpGet("tenants")]
    [Authorize(Policy = Perm.PlatformTenants)]
    [EndpointSummary("List tenants")]
    [EndpointDescription(@"Lists all tenants (cross-tenant) with their company counts.
Filters / params:
- **search**: Optional — free-text on tenant name.
Returns: List<TenantDto>. Requires permission **Platform.Tenants** (Platform Admin only).")]
    public async Task<Result<List<TenantDto>>> Tenants([FromQuery] string? search, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetTenantsQuery(search), ct);
        return Result<List<TenantDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("tenants")]
    [EndpointSummary("Create tenant")]
    [EndpointDescription(@"Creates a new tenant (scope root).
Body:
- **body**: CreateTenantRequest with the tenant name (globally unique).
Returns: GUID of the new tenant; 400 on validation; 409 if the name is taken. Requires permission **Platform.Onboard**.")]
    public async Task<Result<Guid>> CreateTenant([FromBody] CreateTenantRequest body, CancellationToken ct)
    {
        var id = await _mediator.Send(new CreateTenantCommand(body), ct);
        return Result<Guid>.Ok(id, HttpContext.TraceIdentifier);
    }

    [HttpGet("tenants/{tenantId:guid}/companies")]
    [Authorize(Policy = Perm.PlatformTenants)]
    [EndpointSummary("List a tenant's companies")]
    [EndpointDescription(@"Lists the companies (TenantEntities / Infor LN logistic companies) for a given tenant.
Filters / params:
- **tenantId**: Required — tenant GUID.
Returns: List<TenantEntityDto>. Requires permission **Platform.Tenants** (Platform Admin only).")]
    public async Task<Result<List<TenantEntityDto>>> Companies(Guid tenantId, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetTenantEntitiesQuery(tenantId), ct);
        return Result<List<TenantEntityDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("companies")]
    [EndpointSummary("Create company")]
    [EndpointDescription(@"Creates a physical company (TenantEntity) under a tenant.
Body:
- **body**: CreateTenantEntityRequest with target TenantId, company code (unique in the tenant) and name.
Returns: GUID of the new company; 400 on validation; 404 if the tenant is unknown; 409 if the code is taken. Requires permission **Platform.Onboard**.")]
    public async Task<Result<Guid>> CreateCompany([FromBody] CreateTenantEntityRequest body, CancellationToken ct)
    {
        var id = await _mediator.Send(new CreateTenantEntityCommand(body.TenantId, body.Code, body.Name), ct);
        return Result<Guid>.Ok(id, HttpContext.TraceIdentifier);
    }

    [HttpPost("tenant-admins")]
    [EndpointSummary("Create tenant admin")]
    [EndpointDescription(@"Creates the tenant's first Tenant Admin user (TenantId set, MustChangePassword = true).
Body:
- **body**: CreateTenantAdminRequest with target TenantId, user code, full name and email.
Side effects:
- Grants the Admin role, mints a default U-seccode + self SecRight, and clones the default email-template set into the tenant.
Returns: CreateTenantAdminResultDto including a one-time temporary password (shown ONCE); 404 if the tenant is unknown; 409 if user code / email is taken. Requires permission **Platform.Onboard**.")]
    public async Task<Result<CreateTenantAdminResultDto>> CreateTenantAdmin([FromBody] CreateTenantAdminRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateTenantAdminCommand(body), ct);
        return Result<CreateTenantAdminResultDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
