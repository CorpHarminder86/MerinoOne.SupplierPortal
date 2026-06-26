using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.Settings;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R4 (2026-06-26) — Phase 5a (TSD R4 Addendum §7.4 + §8.5 + D5). The admin Settings CRUD surface that backs the
/// Phase 5b Blazor UI: supplier-item over-ship tolerance overrides, the attachment-type catalogue, the
/// attachment-entity reference list, and the two-tier attachment requirement policy grid. Reads are
/// <c>Settings.Read</c>; mutations are <c>Settings.Write</c> (both admin-only per the permission catalogue). Thin —
/// each action only MediatR.Send + Result mapping.
/// </summary>
[ApiController]
[Authorize]
[Route("api/settings")]
public class FulfilmentSettingsController : ControllerBase
{
    private readonly IMediator _mediator;
    public FulfilmentSettingsController(IMediator mediator) => _mediator = mediator;

    // ============================ Supplier-item over-ship tolerance (§7.4) ============================

    [HttpGet("supplier-items")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Supplier item tolerance grid")]
    [EndpointDescription(@"Every active item master joined to the given supplier's over-ship tolerance override, with the RESOLVED tolerance (override ?? master) the ASN guard applies.
Filters / params:
- **supplierId**: Required — the supplier whose grid to load.
Returns: List<SupplierItemToleranceDto> { itemId, itemCode, itemDescription, itemMasterTolerancePct, supplierOverridePct (nullable = inherit), resolvedTolerancePct, supplierItemId (nullable) }. Requires permission **Settings.Read**.")]
    public async Task<Result<List<SupplierItemToleranceDto>>> ListSupplierItemTolerances([FromQuery] Guid supplierId, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSupplierItemTolerancesQuery(supplierId), ct);
        return Result<List<SupplierItemToleranceDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("supplier-items")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Upsert supplier item tolerance")]
    [EndpointDescription(@"Upserts a supplier's over-ship tolerance override for an item, keyed on (supplierId, itemId).
Body:
- **body**: UpsertSupplierItemToleranceRequest { supplierId, itemId, overShipTolerancePct (nullable) }. null = inherit the item-master tolerance (stored as NULL); a value (incl. 0) = explicit cap.
Returns: SupplierItemToleranceDto (with the recomputed resolved tolerance); 400 on validation; 404 unknown supplier/item. Requires permission **Settings.Write**.")]
    public async Task<Result<SupplierItemToleranceDto>> UpsertSupplierItemTolerance([FromBody] UpsertSupplierItemToleranceRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpsertSupplierItemToleranceCommand(body), ct);
        return Result<SupplierItemToleranceDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpDelete("supplier-items/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Delete supplier item tolerance")]
    [EndpointDescription(@"Removes a supplier item tolerance override (reverts that item to inherit the master tolerance).
Filters / params:
- **id**: Required — the SupplierItem row GUID (from the grid's supplierItemId).
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> DeleteSupplierItemTolerance(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeleteSupplierItemToleranceCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    // ============================ Attachment-type catalogue (§8.5) ============================

    [HttpGet("attachment-types")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Attachment type list")]
    [EndpointDescription(@"The tenant's attachment-type catalogue (active + inactive).
Filters / params:
- **isActive**: Optional — true active only, false inactive only, omit for all.
Returns: List<AttachmentTypeDto>. Requires permission **Settings.Read**.")]
    public async Task<Result<List<AttachmentTypeDto>>> ListAttachmentTypes([FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetAttachmentTypesQuery(isActive), ct);
        return Result<List<AttachmentTypeDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("attachment-types")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Create attachment type")]
    [EndpointDescription(@"Creates a new attachment-type catalogue row (code unique per tenant).
Body:
- **body**: CreateAttachmentTypeRequest { code, name }. Code aligns with DocumentUpload.documentType and is immutable post-creation.
Returns: AttachmentTypeDto; 400 on validation; 409 if the code already exists. Requires permission **Settings.Write**.")]
    public async Task<Result<AttachmentTypeDto>> CreateAttachmentType([FromBody] CreateAttachmentTypeRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateAttachmentTypeCommand(body), ct);
        return Result<AttachmentTypeDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("attachment-types/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Update attachment type")]
    [EndpointDescription(@"Renames an attachment type and/or toggles its active flag (no hard delete — deactivate via isActive=false). Code is immutable.
Filters / params:
- **id**: Required — attachment-type GUID.
- **body**: UpdateAttachmentTypeRequest { name, isActive }.
Returns: AttachmentTypeDto; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result<AttachmentTypeDto>> UpdateAttachmentType(Guid id, [FromBody] UpdateAttachmentTypeRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateAttachmentTypeCommand(id, body), ct);
        return Result<AttachmentTypeDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    // ============================ Attachment-entity reference (read-only, §8.5) ============================

    [HttpGet("attachment-entities")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Attachment entity list")]
    [EndpointDescription(@"The tenant's attachment-bearing entity reference list (Supplier / Asn / Invoice). Read-only — seeded, used to drive the policy-grid entity picker.
Filters / params:
- **isActive**: Optional — true active only, false inactive only, omit for all.
Returns: List<AttachmentEntityDto>. Requires permission **Settings.Read**.")]
    public async Task<Result<List<AttachmentEntityDto>>> ListAttachmentEntities([FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetAttachmentEntitiesQuery(isActive), ct);
        return Result<List<AttachmentEntityDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    // ============================ Attachment requirement policy (§8.5 + D5) ============================

    [HttpGet("attachment-policies")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Attachment policy grid")]
    [EndpointDescription(@"The two-tier attachment requirement policy rows for an entity: tenant defaults (supplierId NULL) and, when a supplierId is given, that supplier's overrides. Each row carries the EFFECTIVE requirement (D5 supplier-wins: supplier override ?? tenant default ?? Optional).
Filters / params:
- **entityCode**: Required — Supplier | Asn | Invoice.
- **supplierId**: Optional — include this supplier's overrides; omit for tenant defaults only.
Returns: List<AttachmentPolicyDto> { id, attachmentEntityCode, attachmentTypeId, attachmentTypeCode, attachmentTypeName, supplierId (nullable), requirement, effectiveRequirement, isActive }. Requires permission **Settings.Read**.")]
    public async Task<Result<List<AttachmentPolicyDto>>> ListAttachmentPolicies([FromQuery] string entityCode, [FromQuery] Guid? supplierId, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetAttachmentPoliciesQuery(entityCode, supplierId), ct);
        return Result<List<AttachmentPolicyDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("attachment-policies")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Upsert attachment policy")]
    [EndpointDescription(@"Upserts an attachment requirement policy row, keyed on the D5 unique: (entity, type) for a tenant default (supplierId NULL) or (supplier, entity, type) for a supplier override.
Body:
- **body**: UpsertAttachmentPolicyRequest { attachmentEntityCode, attachmentTypeCode, supplierId (nullable), requirement }. requirement ∈ { Mandatory, Warning, Optional }.
Returns: AttachmentPolicyDto (with effective resolution); 400 on validation; 404 unknown entity/type/supplier. Requires permission **Settings.Write**.")]
    public async Task<Result<AttachmentPolicyDto>> UpsertAttachmentPolicy([FromBody] UpsertAttachmentPolicyRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpsertAttachmentPolicyCommand(body), ct);
        return Result<AttachmentPolicyDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpDelete("attachment-policies/{id:guid}")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Delete attachment policy")]
    [EndpointDescription(@"Removes an attachment requirement policy row (reverts to the tenant default, or to Optional if it was the tenant default).
Filters / params:
- **id**: Required — policy row GUID.
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> DeleteAttachmentPolicy(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeleteAttachmentPolicyCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
