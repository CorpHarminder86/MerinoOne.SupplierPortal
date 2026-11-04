using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Commands;
using MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations.Queries;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R4 (2026-06-24) — PO Negotiation. Thin REST surface over MediatR (mirrors
/// <see cref="SupplierChangeRequestsController"/>).
///
/// <para>R18 (2026-11-03): a negotiation is a chain of ROUNDS, unlimited in number. The supplier opens it (round 1); each round
/// is then answered by the party that did NOT propose it — approve / accept, reject / decline, or counter (a new round). The
/// buyer's actions apply to a supplier-proposed round, the supplier's to a buyer-proposed round; the wrong author is a 409.</para>
///
/// Auth policy split:
/// <list type="bullet">
///   <item><b>PurchaseOrder.Negotiate</b> — supplier raises (create) / withdraws its own round (cancel) / accepts, declines or
///         counters a buyer's round. The create, accept, decline and supplier-counter handlers ALSO run the
///         SupplierWriteGuard (PurchaseOrder.Negotiate permission + a verified SupplierUserMap membership) so the supplier
///         can write THIS portal-originated aggregate without a row-level canWrite grant.</item>
///   <item><b>PurchaseOrder.ApproveNegotiation</b> — buyer approve / reject / counter.</item>
///   <item>List / by-id require <b>PurchaseOrder.Read</b>; seccode RLS scopes a supplier to its own
///         negotiations (a buyer's round shares the supplier's seccode, so the supplier sees it), a buyer's reviewer queue
///         spans the tenant.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize]
[Route("api/purchase-orders/negotiations")]
public class PoNegotiationsController : ControllerBase
{
    private readonly IMediator _mediator;
    public PoNegotiationsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.PurchaseOrderRead)]
    [EndpointSummary("List PO negotiations")]
    [EndpointDescription(@"Lists PO negotiations visible to the caller. Every row is one ROUND of a negotiation (roundNo, proposedBy = Supplier / Buyer).
Filters / params:
- **status**: Optional — Submitted / Approved / Rejected / Cancelled / Countered. Without it, rounds that were Countered (superseded by a newer round) are left out; ask for status=Countered to list them.
Side effects: none. Seccode-scoped — a supplier sees only its own negotiations (including the buyer's rounds on them); a buyer/admin reviewer sees all in the tenant.
Returns: List<PoNegotiationListItemDto> ordered by submittedAt desc. Requires **PurchaseOrder.Read**.")]
    public async Task<Result<List<PoNegotiationListItemDto>>> List([FromQuery] string? status, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPoNegotiationListQuery(status), ct);
        return Result<List<PoNegotiationListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Perm.PurchaseOrderRead)]
    [EndpointSummary("PO negotiation detail")]
    [EndpointDescription(@"Full negotiation detail for the diff view: header + one line per PO line (original → negotiated qty / delivery date; isChanged marks the lines the round changed, the rest echo the PO). Each line carries itemDescription, orderUnit and unitDecimalPlaces. A line's negotiated values are this round's whole effective proposal for it, and previousRoundQty / previousRoundDeliveryDate are the PARENT round's (null on round 1).
Re-negotiation: the id may be ANY round of the negotiation. roundNo / proposedBy / parentNegotiationId place the round in its chain, headNegotiationId is the chain's LATEST round (equal to id when this round is the head), and rounds is the whole chain root -> head (id, roundNo, proposedBy, negotiationStatus, submittedAt / submittedBy, reviewedAt / reviewedBy, notes, rejectionReason).
Filters / params:
- **id**: Required — negotiation (round) GUID.
Returns: PoNegotiationDto; 404 if not found / not visible (seccode). Requires **PurchaseOrder.Read**.")]
    public async Task<Result<PoNegotiationDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPoNegotiationByIdQuery(id), ct);
        return Result<PoNegotiationDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = Perm.PurchaseOrderNegotiate)]
    [EndpointSummary("Raise a PO negotiation")]
    [EndpointDescription(@"Supplier raises a negotiation proposing revised qty / delivery date on PO lines. The live PO lines are NOT mutated. This opens round 1 (proposedBy Supplier); the buyer may then approve, reject or counter it (see /counter).
Body: CreatePoNegotiationRequest (purchaseOrderId, notes?, lines[]). Each line: purchaseOrderLineId, negotiatedQty (>0), negotiatedDeliveryDate?.
Side effects: only lines that actually differ are persisted (delta); captures PreviousPoStatus; flips PO.PoStatus -> Negotiation; stamps Owner = supplier G-seccode. Authorization: PurchaseOrder.Negotiate permission + verified SupplierUserMap membership (403 otherwise).
Returns: PoNegotiationDto; 404 if PO/supplier not found; 409 if PO not Released/Acknowledged; 400 if no line differs / bad input / a changed negotiatedDeliveryDate is earlier than the PO date / a changed negotiatedQty has more decimals than the line's unit allows. Requires **PurchaseOrder.Negotiate**.")]
    public async Task<Result<PoNegotiationDto>> Create([FromBody] CreatePoNegotiationRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreatePoNegotiationCommand(body), ct);
        return Result<PoNegotiationDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Perm.PurchaseOrderNegotiate)]
    [EndpointSummary("Cancel a PO negotiation")]
    [EndpointDescription(@"Supplier withdraws an in-flight negotiation round that IT proposed (Submitted -> Cancelled). The PO reverts to its captured PreviousPoStatus. Nothing is pushed to ERP. A round the buyer proposed is answered with accept / decline / supplier-counter instead.
Filters / params:
- **id**: Required — negotiation GUID.
Returns: empty success; 404 if not found; 409 if not Submitted, if the round was proposed by the Buyer, or if it was answered concurrently (RowVersion). Requires **PurchaseOrder.Negotiate**.")]
    public async Task<Result> Cancel(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new CancelPoNegotiationCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Perm.PurchaseOrderApproveNegotiation)]
    [EndpointSummary("Approve a PO negotiation")]
    [EndpointDescription(@"Buyer approves a submitted negotiation round that the SUPPLIER proposed: negotiation -> Approved, PO.PoStatus -> Approved, and (same transaction) enqueues the ERP round-trip on the outbox (PoNegotiationApprove). The post-commit dispatcher POSTs the negotiated terms + writes the InforSyncLog. Local PO lines are NOT mutated (ERP re-syncs the revised PO inbound). A round the buyer proposed itself is answered by the supplier (accept).
Filters / params:
- **id**: Required — negotiation GUID.
Returns: empty success; 404 if not found; 409 if not Submitted, if the round was proposed by the Buyer, OR a concurrent answer won the RowVersion race. Requires **PurchaseOrder.ApproveNegotiation**.")]
    public async Task<Result> Approve(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new ApprovePoNegotiationCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Perm.PurchaseOrderApproveNegotiation)]
    [EndpointSummary("Reject a PO negotiation")]
    [EndpointDescription(@"Buyer rejects a submitted negotiation round that the SUPPLIER proposed (Submitted -> Rejected, reason stored). The PO reverts to its captured PreviousPoStatus (supplier again sees Ack/Accept/Reject/Negotiate). Nothing is pushed to ERP. A round the buyer proposed itself is answered by the supplier (decline).
Body: RejectPoNegotiationRequest (reason — required, <=1000).
Returns: empty success; 404 if not found; 409 if not Submitted, if the round was proposed by the Buyer, or if it was answered concurrently (RowVersion); 400 if reason missing. Requires **PurchaseOrder.ApproveNegotiation**.")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectPoNegotiationRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectPoNegotiationCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/counter")]
    [Authorize(Policy = Perm.PurchaseOrderApproveNegotiation)]
    [EndpointSummary("Counter a supplier's PO negotiation round")]
    [EndpointDescription(@"Buyer answers an open round that the SUPPLIER proposed with its own revised qty / delivery date instead of approving or rejecting it. The answered round -> Countered and a NEW Submitted round (roundNo + 1, proposedBy Buyer) is opened for the supplier to accept, decline or counter; the number of rounds is unlimited. The PO stays in Negotiation and nothing is pushed to ERP until a round is approved / accepted.
Filters / params:
- **id**: Required — GUID of the supplier's open round.
Body: CounterPoNegotiationRequest (notes?, lines[]). Each line: purchaseOrderLineId, negotiatedQty (>0), negotiatedDeliveryDate?. A line you name overrides the answered round's value for it; a line you omit keeps the answered round's. The price is never countered — the answered round's price carries (negotiatedPrice is ignored). Like every round, only the lines that differ from the live PO are stored, so the round alone carries the whole proposal.
Side effects: one transaction — the answered round is committed as Countered (reviewedAt / reviewedBy stamped), then the new round is inserted under the SAME supplier Owner (seccode) so the supplier keeps seeing it.
Returns: PoNegotiationDto of the NEW round; 404 if not found / not visible; 409 if the round is not Submitted, was proposed by the Buyer, or was answered concurrently (RowVersion); 400 if the counter changes nothing the round proposes (accept it instead), is identical to the PO (reject it instead), names a line that is not on the PO, proposes a delivery earlier than the PO date (when the date changed), or a changed negotiatedQty has more decimals than the line's unit allows. Requires **PurchaseOrder.ApproveNegotiation**.")]
    public async Task<Result<PoNegotiationDto>> Counter(Guid id, [FromBody] CounterPoNegotiationRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CounterPoNegotiationCommand(id, body, NegotiationParty.Buyer), ct);
        return Result<PoNegotiationDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/accept")]
    [Authorize(Policy = Perm.PurchaseOrderNegotiate)]
    [EndpointSummary("Accept the buyer's counter on a PO negotiation")]
    [EndpointDescription(@"Supplier accepts an open round that the BUYER proposed (a counter-offer): negotiation -> Approved, PO.PoStatus -> Approved, and (same transaction) enqueues the same ERP round-trip the buyer's approve does (PoNegotiationApprove, identical key and payload). Local PO lines are NOT mutated (ERP re-syncs the revised PO inbound). Authorization: PurchaseOrder.Negotiate permission + verified SupplierUserMap membership (403 otherwise).
Filters / params:
- **id**: Required — GUID of the buyer's open round.
Returns: PoNegotiationDto (the accepted round); 404 if not found / not visible; 409 if the round is not Submitted, was proposed by the Supplier, or was answered concurrently (RowVersion). Requires **PurchaseOrder.Negotiate**.")]
    public async Task<Result<PoNegotiationDto>> Accept(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new AcceptPoNegotiationCommand(id), ct);
        return Result<PoNegotiationDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/decline")]
    [Authorize(Policy = Perm.PurchaseOrderNegotiate)]
    [EndpointSummary("Decline the buyer's counter on a PO negotiation")]
    [EndpointDescription(@"Supplier declines an open round that the BUYER proposed (Submitted -> Rejected, reason stored). The PO reverts to its captured PreviousPoStatus (the status from before the negotiation began). Nothing is pushed to ERP. Authorization: PurchaseOrder.Negotiate permission + verified SupplierUserMap membership (403 otherwise).
Filters / params:
- **id**: Required — GUID of the buyer's open round.
Body: DeclinePoNegotiationRequest (reason — required, <=1000).
Returns: PoNegotiationDto (the declined round); 404 if not found / not visible; 409 if the round is not Submitted, was proposed by the Supplier, or was answered concurrently (RowVersion); 400 if reason missing. Requires **PurchaseOrder.Negotiate**.")]
    public async Task<Result<PoNegotiationDto>> Decline(Guid id, [FromBody] DeclinePoNegotiationRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new DeclinePoNegotiationCommand(id, body), ct);
        return Result<PoNegotiationDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/supplier-counter")]
    [Authorize(Policy = Perm.PurchaseOrderNegotiate)]
    [EndpointSummary("Counter the buyer's counter on a PO negotiation")]
    [EndpointDescription(@"Supplier answers an open round that the BUYER proposed with its own revised qty / delivery date instead of accepting or declining it. The answered round -> Countered and a NEW Submitted round (roundNo + 1, proposedBy Supplier) is opened for the buyer to approve, reject or counter; the number of rounds is unlimited. The PO stays in Negotiation and nothing is pushed to ERP until a round is approved / accepted.
Filters / params:
- **id**: Required — GUID of the buyer's open round.
Body: CounterPoNegotiationRequest (notes?, lines[]). Each line: purchaseOrderLineId, negotiatedQty (>0), negotiatedDeliveryDate?. A line you name overrides the answered round's value for it; a line you omit keeps the answered round's. The price is never countered — the answered round's price carries (negotiatedPrice is ignored). Like every round, only the lines that differ from the live PO are stored, so the round alone carries the whole proposal.
Side effects: one transaction — the answered round is committed as Countered (reviewedAt / reviewedBy stamped), then the new round is inserted under the same supplier Owner (seccode). Authorization: PurchaseOrder.Negotiate permission + verified SupplierUserMap membership (403 otherwise), and the supplier must be allowed to negotiate (AllowNegotiate).
Returns: PoNegotiationDto of the NEW round; 404 if not found / not visible; 409 if the round is not Submitted, was proposed by the Supplier, the supplier is not permitted to negotiate, or the round was answered concurrently (RowVersion); 400 if the counter changes nothing the round proposes (accept it instead), is identical to the PO (decline it instead), names a line that is not on the PO, proposes a delivery earlier than the PO date (when the date changed), or a changed negotiatedQty has more decimals than the line's unit allows. Requires **PurchaseOrder.Negotiate**.")]
    public async Task<Result<PoNegotiationDto>> SupplierCounter(Guid id, [FromBody] CounterPoNegotiationRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CounterPoNegotiationCommand(id, body, NegotiationParty.Supplier), ct);
        return Result<PoNegotiationDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
