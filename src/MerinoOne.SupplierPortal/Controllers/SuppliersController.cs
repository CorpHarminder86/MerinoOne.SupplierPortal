using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Suppliers.Commands;
using MerinoOne.SupplierPortal.Application.Suppliers.Queries;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/suppliers")]
public class SuppliersController : ControllerBase
{
    private readonly IMediator _mediator;
    public SuppliersController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [EndpointSummary("Supplier list")]
    [EndpointDescription(@"Lists suppliers visible to the caller.
Filters / params:
- **status**: Optional — onboarding/lifecycle status (Pending / Verified / Approved / Rejected / Inactive).
- **search**: Optional — free-text on supplier code / legal name.
- **tenantEntityId**: Optional — restrict to one company (drives the ""select company -> supplier"" mapping UI). Set X-Active-Company to the same company.
Side effects:
- Seccode-scoped: non-privileged users see only their mapped suppliers. Company-scoped: only the active company's suppliers.
Returns: List<SupplierListItemDto> ordered by legal name.")]
    public async Task<Result<List<SupplierListItemDto>>> List(
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] Guid? tenantEntityId,
        CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSupplierListQuery(status, search, tenantEntityId), ct);
        return Result<List<SupplierListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("for-mapping")]
    [Authorize(Policy = "Supplier.Provision")]
    [EndpointSummary("Suppliers for the user-mapping picker")]
    [EndpointDescription(@"Lists a company's suppliers for the admin ""manage supplier maps"" dialog. Unlike GET /api/suppliers,
this is NOT filtered by the header's active company (X-Active-Company) — admin user↔supplier mapping is tenant-wide config,
so the admin can map a user under company 2000 while the header sits on 3000. Tenant-scoped + not-deleted.
Filters / params:
- **tenantEntityId**: Required — the company whose suppliers to list.
- **search**: Optional — free-text on supplier code / legal name.
Returns: List<SupplierListItemDto> ordered by legal name. Requires permission **Supplier.Provision**.")]
    public async Task<Result<List<SupplierListItemDto>>> ForMapping(
        [FromQuery] Guid tenantEntityId,
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSuppliersForMappingQuery(tenantEntityId, search), ct);
        return Result<List<SupplierListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Supplier detail")]
    [EndpointDescription(@"Full supplier profile + verifications + bank/address blocks.
Filters / params:
- **id**: Required — supplier GUID.
Returns: SupplierDetailDto on success; 404 if not found; 403 if seccode mismatch.")]
    public async Task<Result<SupplierDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSupplierByIdQuery(id), ct);
        return Result<SupplierDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/verify-nic")]
    [EndpointSummary("Verify supplier NIC")]
    [EndpointDescription(@"Runs NIC / national-ID verification against the supplier's registered identifiers.
Filters / params:
- **id**: Required — supplier GUID.
Body:
- **body**: VerifyNicRequest with the identifiers to verify.
Side effects:
- Calls the external NIC verification provider and appends results to the supplier's verification trail.
Returns: List<SupplierVerificationDto> with the freshly recorded verification entries; 404 if supplier not found.")]
    public async Task<Result<List<SupplierVerificationDto>>> VerifyNic(Guid id, [FromBody] VerifyNicRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new VerifyNicCommand(id, body), ct);
        return Result<List<SupplierVerificationDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}/verifications")]
    [EndpointSummary("Supplier verifications")]
    [EndpointDescription(@"Returns the verification trail (NIC / KYC / banking) for a supplier.
Filters / params:
- **id**: Required — supplier GUID.
Returns: List<SupplierVerificationDto> ordered chronologically; 404 if supplier not found.")]
    public async Task<Result<List<SupplierVerificationDto>>> Verifications(Guid id, CancellationToken ct)
    {
        var detail = await _mediator.Send(new GetSupplierByIdQuery(id), ct);
        return Result<List<SupplierVerificationDto>>.Ok(detail.Verifications, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [EndpointSummary("Approve supplier")]
    [EndpointDescription(@"Buyer-side approval of a supplier's onboarding submission.
Filters / params:
- **id**: Required — supplier GUID.
Body:
- **body**: ApproveSupplierRequest with approver notes.
Side effects:
- Flips supplier status to Approved + stamps approver/timestamp.
- Triggers downstream Infor master-data sync.
Returns: empty success; 404 if not found; 409 if not in approvable state.")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveSupplierRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveSupplierCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [EndpointSummary("Reject supplier")]
    [EndpointDescription(@"Buyer-side rejection of a supplier onboarding submission.
Filters / params:
- **id**: Required — supplier GUID.
Body:
- **body**: RejectSupplierRequest with required reason.
Side effects:
- Flips status to Rejected + records reason + timestamp.
- Notifies the supplier admin via the configured email template.
Returns: empty success; 404 if not found; 409 if not in rejectable state.")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectSupplierRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectSupplierCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
