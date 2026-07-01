using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Companies.Commands;
using MerinoOne.SupplierPortal.Application.Companies.Queries;
using MerinoOne.SupplierPortal.Application.Platform.Commands;
using MerinoOne.SupplierPortal.Contracts.Companies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Tenant-scoped company (TenantEntity) management. Companies are tenant-scoped CONFIG (not company-scoped),
/// so they are gated by the admin Settings permissions — consistent with the other tenant-wide config
/// surfaces. The always-on tenant filter scopes every read/write to the caller's tenant. Thin — delegates
/// to MediatR.
/// </summary>
[ApiController]
[Authorize]
[Route("api/companies")]
public class CompaniesController : ControllerBase
{
    private readonly IMediator _mediator;
    public CompaniesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("List companies")]
    [EndpointDescription(@"Lists the current tenant's companies (TenantEntities). Drives the active-company selector and company dropdowns.
Filters / params:
- **includeInactive**: Optional — true to include deactivated companies (admin views). Default false.
Returns: List<CompanyDto> scoped to the caller's tenant. Requires permission **Settings.Read**.")]
    public async Task<Result<List<CompanyDto>>> List([FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetCompaniesQuery(includeInactive), ct);
        return Result<List<CompanyDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("accessible")]
    [EndpointSummary("My accessible companies")]
    [EndpointDescription(@"Companies the CURRENT user may act under — drives the header company selector for every
authenticated user (Tenant Admin = all active tenant companies; regular/supplier user = their UserCompanyMap set).
No admin permission required (unlike GET /api/companies). Returns: List<CompanyDto>, tenant-scoped.")]
    public async Task<Result<List<CompanyDto>>> Accessible(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetAccessibleCompaniesQuery(), ct);
        return Result<List<CompanyDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Create company")]
    [EndpointDescription(@"Creates a company in the current tenant (reuses the platform CreateTenantEntity handler with the acting tenant).
Body:
- **body**: CreateCompanyRequest with company code (unique in the tenant) and name.
Returns: GUID of the new company; 400 on validation; 409 if the code is taken. Requires permission **Settings.Write**.")]
    public async Task<Result<Guid>> Create([FromBody] CreateCompanyRequest body, CancellationToken ct)
    {
        var tenantId = User.FindFirst("tenant")?.Value;
        if (!Guid.TryParse(tenantId, out var tid))
            return Result<Guid>.Fail("The current session has no tenant context.");

        var id = await _mediator.Send(new CreateTenantEntityCommand(tid, body.Code, body.Name), ct);
        return Result<Guid>.Ok(id, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Update company")]
    [EndpointDescription(@"Renames / re-codes a company in the current tenant.
Filters / params:
- **id**: Required — company GUID.
Body:
- **body**: UpdateCompanyRequest with the new code + name.
Returns: empty success; 404 if not found; 409 if the new code collides. Requires permission **Settings.Write**.")]
    public async Task<Result> Update(Guid id, [FromBody] UpdateCompanyRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateCompanyCommand(id, body.Code, body.Name), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Deactivate company")]
    [EndpointDescription(@"Deactivates a company — drops it from the active-company selector while preserving its data.
Filters / params:
- **id**: Required — company GUID.
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> Deactivate(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new SetCompanyActiveCommand(id, false), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Reactivate company")]
    [EndpointDescription(@"Reactivates a previously deactivated company.
Filters / params:
- **id**: Required — company GUID.
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> Reactivate(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new SetCompanyActiveCommand(id, true), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
