using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Shipments.Commands;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Shipments.AsnListItemDto>;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/asns")]
public class AsnsController : ControllerBase
{
    private readonly IMediator _mediator;
    public AsnsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Asn.Read")]
    [EndpointSummary("ASN list")]
    [EndpointDescription(@"Paged list of Advance Shipment Notices (ASNs) visible to the caller.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **status**: Optional — ASN lifecycle status filter.
- **supplierId**: Optional — restrict to one supplier.
- **purchaseOrderId**: Optional — restrict to one PO.
- **search**: Optional — free-text on ASN number / reference.
Side effects:
- Seccode-scoped: non-privileged users see only their suppliers' ASNs.
Returns: PagedResult<AsnListItemDto>. Requires permission **Asn.Read**.")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] Guid? purchaseOrderId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetAsnListQuery(page, pageSize, status, supplierId, purchaseOrderId, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Asn.Read")]
    [EndpointSummary("ASN detail")]
    [EndpointDescription(@"Full ASN header + line items + linked PO references.
Filters / params:
- **id**: Required — ASN GUID.
Returns: AsnDetailDto on success; 404 if not found; 403 if seccode mismatch. Requires permission **Asn.Read**.")]
    public async Task<Result<AsnDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetAsnByIdQuery(id), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "Asn.Write")]
    [EndpointSummary("Create ASN (Draft)")]
    [EndpointDescription(@"Supplier creates a DRAFT ASN spanning one or more POs. NO ERP post on create.
Body:
- **body**: CreateAsnRequest — PurchaseOrderId (single) or PurchaseOrderIds (multi), ship lines, carrier metadata.
Side effects:
- Creates the ASN in Draft, populates the AsnPurchaseOrder junction, snapshots each line's PositionNo/SequenceNo.
Returns: AsnDetailDto (Draft) on success; 400 on validation; 403 if seccode mismatch. Requires **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> Create([FromBody] CreateAsnRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateAsnCommand(body), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Asn.Write")]
    [EndpointSummary("Update ASN (Draft only)")]
    [EndpointDescription(@"Edits a DRAFT ASN (header + lines). Rejected (409) once the ASN is Submitted/Cancelled
(lock-on-submit). Replaces the line set, re-snapshots PositionNo/SequenceNo, rebuilds the multi-PO junction.
Returns: AsnDetailDto on success; 404 if not found; 409 if not Draft. Requires **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> Update(Guid id, [FromBody] UpdateAsnRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateAsnCommand(id, body), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = "Asn.Write")]
    [EndpointSummary("Submit ASN")]
    [EndpointDescription(@"Submits a DRAFT ASN (Draft -> Submitted). In ONE transaction: enforces the PO confirmation
gate per covered PO; validates over-ship, lot/serial (per Item flags) and the single-currency guard; stamps
submittedAt/by + erpSyncId; creates EXACTLY ONE draft Invoice spanning all the ASN's POs; enqueues the ASN->ERP
post on the outbox (dispatched post-commit). Locks the ASN (and its attachments) against further edits.
Body:
- **body**: Optional SubmitAsnRequest — OverrideReason, used only by a caller holding PurchaseOrder.OverrideGate to
  ship despite a blocking PO confirmation gate (audited); AcknowledgeMissingAttachments confirms proceeding past a
  Warning-level attachment requirement (§8.3).
Attachment governance (§8.3): a missing MANDATORY attachment blocks (400, message names the types). A missing
WARNING attachment on the first call returns 200 with **confirmationRequired=true** + confirmationMessage +
missingAttachments (NOT an error, nothing committed); re-submit with AcknowledgeMissingAttachments=true to proceed
(the skip is audited).
Returns: AsnDetailDto (Submitted, with DraftInvoiceId) on success; 200 confirmationRequired on a Warning skip; 400 on
validation / gate block / mandatory-missing; 409 if not Draft. Requires **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> Submit(Guid id, [FromBody] SubmitAsnRequest? body, CancellationToken ct)
    {
        var outcome = await _mediator.Send(
            new SubmitAsnCommand(id, body?.OverrideReason, body?.AcknowledgeMissingAttachments ?? false), ct);
        return outcome.ToResult(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "Asn.Write")]
    [EndpointSummary("Cancel ASN")]
    [EndpointDescription(@"Cancels an ASN (Draft or Submitted -> Cancelled). Terminal for supplier edits.
Returns: empty success; 404 if not found; 409 if already Cancelled. Requires **Asn.Write**.")]
    public async Task<Result> Cancel(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new CancelAsnCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
