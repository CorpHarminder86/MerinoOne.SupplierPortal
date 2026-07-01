using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Suppliers.Commands;
using MerinoOne.SupplierPortal.Application.Suppliers.Queries;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

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
    [Authorize(Policy = Perm.SupplierProvision)]
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
    [Authorize(Policy = Perm.SupplierApprove)]
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
    [Authorize(Policy = Perm.SupplierApprove)]
    [EndpointSummary("Approve supplier")]
    [EndpointDescription(@"Buyer-side approval of a supplier's onboarding submission.
Filters / params:
- **id**: Required — supplier GUID.
Body:
- **body**: ApproveSupplierRequest with approver notes + AcknowledgeMissingAttachments (§8.3).
Side effects:
- Flips supplier status to Approved + stamps approver/timestamp.
- Triggers downstream Infor master-data sync.
Attachment governance (§8.3): a missing MANDATORY supplier attachment blocks (400, message names the types). A
missing WARNING attachment on the first call returns 200 with confirmationRequired=true + confirmationMessage +
missingAttachments (NOT an error, supplier NOT approved); re-approve with AcknowledgeMissingAttachments=true to
proceed (the skip is audited).
Returns: empty success; 200 confirmationRequired on a Warning skip; 404 if not found; 400 on mandatory-attachment
missing; 409 if not in approvable state.")]
    public async Task<Result<object?>> Approve(Guid id, [FromBody] ApproveSupplierRequest body, CancellationToken ct)
    {
        var outcome = await _mediator.Send(new ApproveSupplierCommand(id, body), ct);
        return outcome.IsCompleted
            ? Result<object?>.Ok(null, HttpContext.TraceIdentifier)
            : Result<object?>.NeedsConfirmation(
                outcome.ConfirmationMessage!, outcome.MissingWarning, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Perm.SupplierApprove)]
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

    // ---------------- Bank details (R4 Module 1) ----------------

    [HttpPost("{id:guid}/bank-details")]
    [Authorize(Policy = Perm.SupplierWrite)]
    [EndpointSummary("Add supplier bank detail")]
    [EndpointDescription(@"Adds a bank account to a supplier. Seccode.canWrite is enforced in the handler (403 on mismatch).
Body: AddSupplierBankDetailRequest — all six fields required (bankName, bankAddress, accountName, accountNumber, currencyId, ifscCode);
IFSC must match ^[A-Z]{4}0[A-Z0-9]{6}$ and is required for INR. Returns SupplierBankDetailDto. Requires **Supplier.Write**.")]
    public async Task<Result<SupplierBankDetailDto>> AddBankDetail(Guid id, [FromBody] AddSupplierBankDetailRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new AddSupplierBankDetailCommand(id, body), ct);
        return Result<SupplierBankDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}/bank-details/{bankDetailId:guid}")]
    [Authorize(Policy = Perm.SupplierWrite)]
    [EndpointSummary("Update supplier bank detail")]
    [EndpointDescription(@"Updates a supplier bank account. canWrite-gated (403). Returns SupplierBankDetailDto; 404 if not found. Requires **Supplier.Write**.")]
    public async Task<Result<SupplierBankDetailDto>> UpdateBankDetail(Guid id, Guid bankDetailId, [FromBody] UpdateSupplierBankDetailRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateSupplierBankDetailCommand(id, bankDetailId, body), ct);
        return Result<SupplierBankDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}/bank-details/{bankDetailId:guid}")]
    [Authorize(Policy = Perm.SupplierWrite)]
    [EndpointSummary("Delete supplier bank detail")]
    [EndpointDescription(@"Soft-deletes a supplier bank account. canWrite-gated (403). Returns empty success; 404 if not found. Requires **Supplier.Write**.")]
    public async Task<Result> DeleteBankDetail(Guid id, Guid bankDetailId, CancellationToken ct)
    {
        await _mediator.Send(new DeleteSupplierBankDetailCommand(id, bankDetailId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    // ---------------- Licenses (R4 Module 1) ----------------

    [HttpPost("{id:guid}/licenses")]
    [Authorize(Policy = Perm.SupplierWrite)]
    [EndpointSummary("Add supplier license")]
    [EndpointDescription(@"Adds a license / certification to a supplier. canWrite-gated (403). ExpiryDate must be >= IssueDate.
Body: AddSupplierLicenseRequest. Returns SupplierLicenseDto. Requires **Supplier.Write**.")]
    public async Task<Result<SupplierLicenseDto>> AddLicense(Guid id, [FromBody] AddSupplierLicenseRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new AddSupplierLicenseCommand(id, body), ct);
        return Result<SupplierLicenseDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}/licenses/{licenseId:guid}")]
    [Authorize(Policy = Perm.SupplierWrite)]
    [EndpointSummary("Update supplier license")]
    [EndpointDescription(@"Updates a supplier license. canWrite-gated (403). ExpiryDate must be >= IssueDate. Returns SupplierLicenseDto; 404 if not found. Requires **Supplier.Write**.")]
    public async Task<Result<SupplierLicenseDto>> UpdateLicense(Guid id, Guid licenseId, [FromBody] UpdateSupplierLicenseRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateSupplierLicenseCommand(id, licenseId, body), ct);
        return Result<SupplierLicenseDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpDelete("{id:guid}/licenses/{licenseId:guid}")]
    [Authorize(Policy = Perm.SupplierWrite)]
    [EndpointSummary("Delete supplier license")]
    [EndpointDescription(@"Soft-deletes a supplier license. canWrite-gated (403). Returns empty success; 404 if not found. Requires **Supplier.Write**.")]
    public async Task<Result> DeleteLicense(Guid id, Guid licenseId, CancellationToken ct)
    {
        await _mediator.Send(new DeleteSupplierLicenseCommand(id, licenseId), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpGet("licenses/expiring")]
    [Authorize(Policy = Perm.SupplierRead)]
    [EndpointSummary("Expiring supplier licenses")]
    [EndpointDescription(@"Licenses expiring within N days (default 90) for the expiry-reminder dashboard. Seccode-scoped:
suppliers see only their own. Filters / params: **withinDays** (optional). Returns List<SupplierLicenseExpiringDto>. Requires **Supplier.Read**.")]
    public async Task<Result<List<SupplierLicenseExpiringDto>>> ExpiringLicenses([FromQuery] int withinDays = 90, CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetSupplierLicensesExpiringQuery(withinDays), ct);
        return Result<List<SupplierLicenseExpiringDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    // ---------------- PO-response mode (R4 Module 1, admin) ----------------

    [HttpPost("{id:guid}/po-response-mode")]
    [Authorize(Policy = Perm.SupplierApprove)]
    [EndpointSummary("Set supplier PO-confirmation mode")]
    [EndpointDescription(@"Admin sets a supplier's PO confirmation mode (AutoAccept / AcknowledgeToShip / AcceptToShip)
plus the AllowNegotiate / AllowReject action toggles. Editable post-approval.
Body: SetPoResponseModeRequest. Returns empty success; 404 if not found; 400 on invalid mode. Requires **Supplier.Approve**.")]
    public async Task<Result> SetPoResponseMode(Guid id, [FromBody] SetPoResponseModeRequest body, CancellationToken ct)
    {
        await _mediator.Send(new SetSupplierPoResponseModeCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    // ---------------- Commercial terms (R4 #1, internal) ----------------

    [HttpPut("{id:guid}/commercial-terms")]
    [Authorize(Policy = Perm.SupplierApprove)]
    [EndpointSummary("Set supplier commercial terms")]
    [EndpointDescription(@"Internal user sets a supplier's Currency + Payment/Delivery term FKs (R4 #1); the handler snapshots the term codes. Any field may be null to clear it.
Body: UpdateCommercialTermsRequest. Returns empty success; 404 if not found. Requires **Supplier.Approve**.")]
    public async Task<Result> SetCommercialTerms(Guid id, [FromBody] UpdateCommercialTermsRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateSupplierCommercialTermsCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
