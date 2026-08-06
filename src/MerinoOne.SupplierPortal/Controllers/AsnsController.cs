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
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/asns")]
public class AsnsController : ControllerBase
{
    private readonly IMediator _mediator;
    public AsnsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.AsnRead)]
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
    [Authorize(Policy = Perm.AsnApprove)]
    [EndpointSummary("ASN approval queue")]
    [EndpointDescription(@"R5 (review gap C2) — the ASNs awaiting approval, scoped by the SUPPLIER-USER MAPPING: an
internal approver (all callers hold Asn.Approve — the endpoint is policy-gated) sees the PendingApproval ASNs of the
suppliers they are mapped to (admin.SupplierUserMap), so multiple buyers with hierarchy access each see their own
suppliers' ASNs. An Admin sees ALL. (Per-buyer routing by PurchaseOrder.BuyerUserId was removed — nothing populates
it.) Tenant-scoped. Ordered by the latest Pending approval's SubmittedOn DESC.
Returns: Result<List<AsnApprovalListItemDto>>. Requires permission **Asn.Approve** (same policy as approve/reject).")]
    public async Task<Result<List<AsnApprovalListItemDto>>> PendingApprovals(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPendingAsnApprovalsQuery(), ct);
        return Result<List<AsnApprovalListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Perm.AsnRead)]
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
    [Authorize(Policy = Perm.AsnWrite)]
    [EndpointSummary("Create ASN (Draft)")]
    [EndpointDescription(@"Supplier creates a DRAFT ASN spanning one or more POs. NO ERP post on create.
Body:
- **body**: CreateAsnRequest — PurchaseOrderId (single) or PurchaseOrderIds (multi), ship lines, carrier metadata.
  R15 — each line may carry an optional **deliveryScheduleId** back-link; when supplied it must reference a live
  delivery schedule of the SAME purchaseOrderLineId (else 400), and each schedule may appear on at most one line.
Side effects:
- Creates the ASN in Draft, populates the AsnPurchaseOrder junction, snapshots each line's PositionNo/SequenceNo.
Returns: AsnDetailDto (Draft) on success; 400 on validation; 403 if seccode mismatch. Requires **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> Create([FromBody] CreateAsnRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateAsnCommand(body), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Perm.AsnWrite)]
    [EndpointSummary("Update ASN (Draft only)")]
    [EndpointDescription(@"Edits a DRAFT ASN (header + lines). Rejected (409) once the ASN is Submitted/Cancelled
(lock-on-submit). Replaces the line set, re-snapshots PositionNo/SequenceNo, rebuilds the multi-PO junction.
R15 — each line may carry an optional **deliveryScheduleId** back-link (validated like Create: live schedule of the
same PO line, unique per request); the replace-set persists it, so draft saves no longer wipe schedule provenance.
Returns: AsnDetailDto on success; 404 if not found; 409 if not Draft. Requires **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> Update(Guid id, [FromBody] UpdateAsnRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateAsnCommand(id, body), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("from-schedule")]
    [Authorize(Policy = Perm.AsnWrite)]
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
    [Authorize(Policy = Perm.AsnWrite)]
    [EndpointSummary("Send ASN for approval")]
    [EndpointDescription(@"R5 §10.2, re-cut by R14 — supplier sends a DRAFT ASN for buyer confirmation
(Draft -> PendingApproval). Creates an AsnApproval (Pending) routed to the ASN's mapped internal user(s).

R14: this step is deliberately PERMISSIVE. Attachments are NOT checked here and the invoiceNo / billOfLading /
packingList references are NOT required here — both moved to POST — because a supplier must be able to obtain
buyer confirmation before the Packing List and Commercial Invoice exist. Only the PO ship gate applies.

Rejected with 400 (asnConfirmation) for a supplier whose Supplier Master has ASN Confirmation Required = No:
that supplier has no approval step and must call POST /post directly.
Returns: AsnDetailDto (PendingApproval) on success; 400 if the supplier needs no confirmation or a covered PO is
not shippable; 409 if not Draft. Requires **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> SendForApproval(Guid id, [FromBody] SendForApprovalRequest? body, CancellationToken ct)
    {
        var outcome = await _mediator.Send(
            new SendForApprovalCommand(id, body?.AcknowledgeMissingAttachments ?? false), ct);
        return outcome.ToResult(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Perm.AsnApprove)]
    [EndpointSummary("Confirm ASN (buyer)")]
    [EndpointDescription(@"R5 §10.2, re-cut by R14 — a mapped internal user confirms a PendingApproval ASN
(PendingApproval -> **Approved**). Any ONE mapped user may confirm.

R14: this NO LONGER submits anything to the ERP. It records the buyer's confirmation and stops. No balance is
consumed, no draft invoice is created and nothing is enqueued to LN — all of that now happens when the SUPPLIER
calls POST /post. The ASN becomes editable again so the supplier can upload the Packing List / Commercial Invoice
and complete the shipment references.

OverrideReason on the body is accepted but IGNORED (the PO gate override now belongs to the Post caller).
Returns: AsnDetailDto (Approved) on success; 403 if not a mapped internal user; 409 if not PendingApproval.
Requires **Asn.Approve**.")]
    public async Task<Result<AsnDetailDto>> Approve(Guid id, [FromBody] ApproveAsnRequest? body, CancellationToken ct)
    {
        var data = await _mediator.Send(new ApproveAsnCommand(id, body?.OverrideReason), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/post")]
    [Authorize(Policy = Perm.AsnPost)]
    [EndpointSummary("Post ASN to the ERP")]
    [EndpointDescription(@"R14 — the supplier's final action and the ONLY path that reaches the ERP.
From-state: **Approved** (for a supplier with ASN Confirmation Required = Yes) or **Draft** (for one with No,
which has no approval step at all). Approved is postable in both modes so flipping the flag never strands an ASN.

Enforced here (both moved from send-for-approval): invoiceNo / billOfLading / packingList are MANDATORY (400,
shipmentReferences); and the attachment policy — a missing MANDATORY attachment blocks (400, names the types),
a missing WARNING attachment on the first call returns 200 with **confirmationRequired=true** + missingAttachments
and commits nothing (re-send with AcknowledgeMissingAttachments=true to proceed; the skip is audited).

On success: stamps postedAt/postedBy (**postedAt IS the shipping date**), notifies the mapped buyers, then runs
the submit path — the over-ship atomic guard consumes balance, the ASN flips to Submitted, submittedAt is stamped
with the same instant (so the LN payload's ShipmentDate is the post date), the grouped draft invoice(s) are
created and the gated LN outbox message is enqueued. If balance was lost since confirmation the guard returns 0
rows and the whole post fails (400) with the ASN left untouched.

If a covered PO was reset to an unshippable status after confirmation, the PO confirmation gate blocks (400,
poStatus); a caller holding **PurchaseOrder.OverrideGate** may supply OverrideReason to proceed (audited).
Returns: AsnDetailDto (Submitted) on success; 200 confirmationRequired on a Warning skip; 400 on missing refs /
mandatory attachments / over-ship / PO gate; 409 from an illegal state. Requires **Asn.Post**.")]
    public async Task<Result<AsnDetailDto>> Post(Guid id, [FromBody] PostAsnRequest? body, CancellationToken ct)
    {
        var outcome = await _mediator.Send(
            new PostAsnCommand(id, body?.AcknowledgeMissingAttachments ?? false, body?.OverrideReason), ct);
        return outcome.ToResult(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Perm.AsnApprove)]
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
    [Authorize(Policy = Perm.AsnWrite)]
    [EndpointSummary("Cancel ASN")]
    [EndpointDescription(@"Cancels an ASN (Draft or Submitted -> Cancelled). Terminal for supplier edits.
Returns: empty success; 404 if not found; 409 if already Cancelled. Requires **Asn.Write**.")]
    public async Task<Result> Cancel(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new CancelAsnCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
