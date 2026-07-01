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

    [HttpGet("pending-approvals")]
    [Authorize(Policy = "Asn.Approve")]
    [EndpointSummary("ASN approval queue")]
    [EndpointDescription(@"R5 (review gap C2) — the ASNs awaiting approval. Like the PO-negotiation reviewer queue,
any caller (all callers hold Asn.Approve — the endpoint is policy-gated) sees ALL ASNs in AsnStatus=PendingApproval
in the tenant. (Per-buyer routing was removed — nothing populates PurchaseOrder.BuyerUserId, so it always yielded
an empty buyer queue.) Tenant-scoped (a reviewer holds no supplier seccode, so the seccode/company filters are
bypassed and re-scoped to the caller's tenant). Ordered by the latest Pending approval's SubmittedOn DESC.
Returns: Result<List<AsnApprovalListItemDto>>. Requires permission **Asn.Approve** (same policy as approve/reject).")]
    public async Task<Result<List<AsnApprovalListItemDto>>> PendingApprovals(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPendingAsnApprovalsQuery(), ct);
        return Result<List<AsnApprovalListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
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

    [HttpPost("from-schedule")]
    [Authorize(Policy = "Asn.Write")]
    [EndpointSummary("Create ASN from delivery schedules (Draft)")]
    [EndpointDescription(@"R5 §9 — supplier creates a DRAFT ASN from selected Delivery Schedule lines. All selected
schedules must share ONE ship-to (cross-ship-to is blocked, UC-AS-02) and ONE supplier; lines may span multiple POs
(UC-AS-01). The header is grouped by (supplier, ship-to); each schedule becomes an AsnLine referencing its
purchaseOrderLineId + deliveryScheduleId, ship qty defaulted to the line's remaining balance (editable, §9.2).
NO balance is consumed at create — the over-ship guard runs only at final Submit (Approve).
Returns: AsnDetailDto (Draft) on success; 400 on cross-ship-to / multi-supplier / invariant; 404 if a schedule is
missing. Requires **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> CreateFromSchedule([FromBody] CreateAsnFromScheduleRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateAsnFromScheduleCommand(body), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/send-for-approval")]
    [Authorize(Policy = "Asn.Write")]
    [EndpointSummary("Send ASN for approval")]
    [EndpointDescription(@"R5 §10.2/§10.3 — supplier sends a DRAFT ASN for buyer approval (Draft -> PendingApproval).
Runs the attachment-requirement check HERE (moved from Submit): a missing MANDATORY attachment blocks (400, names
the types); a missing WARNING attachment on the first call returns 200 with **confirmationRequired=true** +
missingAttachments (nothing committed) — re-send with AcknowledgeMissingAttachments=true to proceed (the skip is
audited). Creates an AsnApproval (Pending) routed to the ASN's distinct PO buyer(s).
Returns: AsnDetailDto (PendingApproval) on success; 200 confirmationRequired on a Warning skip; 400 on
mandatory-missing; 409 if not Draft. Requires **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> SendForApproval(Guid id, [FromBody] SendForApprovalRequest? body, CancellationToken ct)
    {
        var outcome = await _mediator.Send(
            new SendForApprovalCommand(id, body?.AcknowledgeMissingAttachments ?? false), ct);
        return outcome.ToResult(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "Asn.Approve")]
    [EndpointSummary("Approve ASN (buyer)")]
    [EndpointDescription(@"R5 §10.2/§10.4 — a mapped PO buyer approves a PendingApproval ASN. Any ONE of the ASN's PO
buyers may approve (Phase 1). On approve the system runs the SUBMIT path: the over-ship atomic guard consumes
balance, the ASN flips to Submitted, the draft Invoice + ERP outbox post are created — exactly as the R4 submit did.
If the balance was lost after approval (UC-AP-05) the guard returns 0 rows and Submit fails (400, over-ship message)
while the ASN stays PendingApproval.
Returns: AsnDetailDto (Submitted, with DraftInvoiceId) on success; 400 on over-ship/serial-lot; 403 if not a mapped
buyer; 409 if not PendingApproval. Requires **Asn.Approve**.")]
    public async Task<Result<AsnDetailDto>> Approve(Guid id, [FromBody] ApproveAsnRequest? body, CancellationToken ct)
    {
        var data = await _mediator.Send(new ApproveAsnCommand(id, body?.OverrideReason), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "Asn.Approve")]
    [EndpointSummary("Reject ASN (buyer)")]
    [EndpointDescription(@"R5 §10.2 — a mapped PO buyer rejects a PendingApproval ASN with a MANDATORY reason
(PendingApproval -> Rejected). No balance was consumed, so no reversal is needed; the supplier edits the ASN
(returning it to Draft) and re-raises. The supplier is notified with the reason (best-effort).
Returns: AsnDetailDto (Rejected) on success; 400 if reason missing; 403 if not a mapped buyer; 409 if not
PendingApproval. Requires **Asn.Approve**.")]
    public async Task<Result<AsnDetailDto>> Reject(Guid id, [FromBody] RejectAsnRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new RejectAsnCommand(id, body.Reason), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
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
