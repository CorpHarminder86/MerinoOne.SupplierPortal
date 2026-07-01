using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.Settings;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

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
    [Authorize(Policy = Perm.SettingsRead)]
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
    [Authorize(Policy = Perm.SettingsWrite)]
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
    [Authorize(Policy = Perm.SettingsWrite)]
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
    [Authorize(Policy = Perm.SettingsRead)]
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
    [Authorize(Policy = Perm.SettingsWrite)]
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
    [Authorize(Policy = Perm.SettingsWrite)]
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
    [Authorize(Policy = Perm.SettingsRead)]
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
    [Authorize(Policy = Perm.SettingsRead)]
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
    [Authorize(Policy = Perm.SettingsWrite)]
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
    [Authorize(Policy = Perm.SettingsWrite)]
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

    // ============================ Company ship-to addresses (R5 §5 / §6.1 — Component 1 / consolidation) ===========
    // The named, ERP-mappable ship-to addresses hung off a company = admin.TenantEntity (the duplicate admin.Company
    // was dropped; the company list itself is served by /api/companies over TenantEntity). erpCode resolves the
    // inbound PO ship-to (§6.2). The customer name on the PO header derives from TenantEntity.Name. Routes live under
    // /api/settings/companies/{companyId}/addresses (companyId = the TenantEntity id).

    [HttpGet("companies/{companyId:guid}/addresses")]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("Company address list")]
    [EndpointDescription(@"The named, ERP-mappable ship-to addresses under a company (TenantEntity).
Filters / params:
- **companyId**: Required — owning company (TenantEntity) GUID.
- **isActive**: Optional — true active only, false inactive only, omit for all.
Returns: List<CompanyAddressDto> { id, seq, companyId, addressName, erpCode, addressType, addressLine1/2, city, state, pincode, country, isActive, createdOn }; 404 unknown company. Requires permission **Settings.Read**.")]
    public async Task<Result<List<CompanyAddressDto>>> ListCompanyAddresses(Guid companyId, [FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetCompanyAddressesQuery(companyId, isActive), ct);
        return Result<List<CompanyAddressDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("company-addresses")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Create company address")]
    [EndpointDescription(@"Creates a ship-to address under a Company. AddressName is REQUIRED; ErpCode is OPTIONAL but must be unique within the company when present (the inbound PO ship-to resolves against it).
Body:
- **body**: CreateCompanyAddressRequest { companyId, addressName, erpCode?, addressType, addressLine1, addressLine2?, city, state, pincode?, country? }.
Returns: CompanyAddressDto; 400 on validation; 404 unknown company; 409 if the erpCode collides within the company. Requires permission **Settings.Write**.")]
    public async Task<Result<CompanyAddressDto>> CreateCompanyAddress([FromBody] CreateCompanyAddressRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateCompanyAddressCommand(body), ct);
        return Result<CompanyAddressDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("company-addresses/{id:guid}")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Update company address")]
    [EndpointDescription(@"Edits a ship-to address (deactivate via isActive=false — kept for historical POs, removed from new ship-to resolution). ErpCode stays unique per company when present.
Filters / params:
- **id**: Required — CompanyAddress GUID.
- **body**: UpdateCompanyAddressRequest { addressName, erpCode?, addressType, addressLine1, addressLine2?, city, state, pincode?, country?, isActive }.
Returns: CompanyAddressDto; 404 if not found; 409 if the erpCode collides within the company. Requires permission **Settings.Write**.")]
    public async Task<Result<CompanyAddressDto>> UpdateCompanyAddress(Guid id, [FromBody] UpdateCompanyAddressRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateCompanyAddressCommand(id, body), ct);
        return Result<CompanyAddressDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    // ============================ ERP→portal PO status mapping (R5 §4.7 / §11 — Component 7) ============================
    // The configurable ERP→portal PO-status translation (replaces the R4 hardcoded Modify→Released). Many ERP
    // statuses may map to ONE portal status; each erpStatus is unique per tenant. Targets are restricted to the
    // ERP-driven subset Draft | Released | Cancelled | Closed | Delivered (§11.2). Resolution is case-insensitive.

    [HttpGet("po-status-mappings")]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("PO status mapping list")]
    [EndpointDescription(@"The tenant's ERP→portal PO-status mapping rows. Each maps one raw ERP status to one portal PoStatus; the inbound PO sync resolves the incoming erpStatus against this (case-insensitive).
Filters / params:
- **isActive**: Optional — true active only, false inactive only, omit for all.
Returns: List<PoStatusMappingDto> { id, seq, erpStatus, poStatus, isActive, createdOn }. Requires permission **Settings.Read**.")]
    public async Task<Result<List<PoStatusMappingDto>>> ListPoStatusMappings([FromQuery] bool? isActive, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPoStatusMappingsQuery(isActive), ct);
        return Result<List<PoStatusMappingDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("po-status-mappings")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Create PO status mapping")]
    [EndpointDescription(@"Creates an ERP→portal PO-status mapping (erpStatus unique per tenant; the inbound sync resolves against it).
Body:
- **body**: CreatePoStatusMappingRequest { erpStatus, poStatus }. poStatus MUST be one of the ERP-driven targets: Draft | Released | Cancelled | Closed | Delivered (§11.2 — supplier/fulfilment-driven statuses are rejected).
Returns: PoStatusMappingDto; 400 on validation; 409 if the erpStatus is already mapped. Requires permission **Settings.Write**.")]
    public async Task<Result<PoStatusMappingDto>> CreatePoStatusMapping([FromBody] CreatePoStatusMappingRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreatePoStatusMappingCommand(body), ct);
        return Result<PoStatusMappingDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("po-status-mappings/{id:guid}")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Update PO status mapping")]
    [EndpointDescription(@"Re-targets a mapping's portal status and/or toggles its active flag (erpStatus is immutable — it is the lookup key).
Filters / params:
- **id**: Required — mapping GUID.
- **body**: UpdatePoStatusMappingRequest { poStatus, isActive }. poStatus restricted to the ERP-driven subset (§11.2).
Returns: PoStatusMappingDto; 400 on validation; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result<PoStatusMappingDto>> UpdatePoStatusMapping(Guid id, [FromBody] UpdatePoStatusMappingRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdatePoStatusMappingCommand(id, body), ct);
        return Result<PoStatusMappingDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpDelete("po-status-mappings/{id:guid}")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Delete PO status mapping")]
    [EndpointDescription(@"Deactivates (soft-deletes) a mapping row — the erpStatus stops resolving and is freed for a future re-add. No hard delete.
Filters / params:
- **id**: Required — mapping GUID.
Returns: empty success; 404 if not found. Requires permission **Settings.Write**.")]
    public async Task<Result> DeletePoStatusMapping(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new DeletePoStatusMappingCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
