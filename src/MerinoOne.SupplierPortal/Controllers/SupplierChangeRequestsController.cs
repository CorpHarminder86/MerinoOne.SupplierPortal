using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Commands;
using MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests.Queries;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R4 Module 2 — Supplier Change Management. Thin REST surface over MediatR.
///
/// Auth policy split:
/// <list type="bullet">
///   <item><b>Supplier.ChangeRequest</b> — supplier create / update-lines / submit. The handler ALSO runs the
///         SupplierWriteGuard (Supplier.ChangeRequest permission + verified SupplierUserMap membership) so the
///         supplier can write THIS portal-originated aggregate without a row-level canWrite grant.</item>
///   <item><b>Supplier.ApproveChange</b> — reviewer approve / reject / request-changes.</item>
///   <item>List / by-id require only authentication; seccode RLS scopes a supplier to its own requests.</item>
/// </list>
/// </summary>
[ApiController]
[Authorize]
[Route("api/suppliers/change-requests")]
public class SupplierChangeRequestsController : ControllerBase
{
    private readonly IMediator _mediator;
    public SupplierChangeRequestsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [EndpointSummary("List supplier change requests")]
    [EndpointDescription(@"Lists supplier change requests visible to the caller.
Filters / params:
- **supplierId**: Optional — restrict to one supplier.
- **status**: Optional — Draft / Submitted / UnderReview / ChangesRequested / Approved / Rejected / Pushed / PartiallyPushed / PushFailed.
Side effects: none. Seccode-scoped — a supplier sees only its own requests; internal users see all.
Returns: List<SupplierChangeRequestListItemDto> ordered by requestedAt desc.")]
    public async Task<Result<List<SupplierChangeRequestListItemDto>>> List(
        [FromQuery] Guid? supplierId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSupplierChangeRequestListQuery(supplierId, status), ct);
        return Result<List<SupplierChangeRequestListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Supplier change request detail")]
    [EndpointDescription(@"Full change-request detail for the diff view: header + lines (old→new per Edit line, payloadJson per Add, target id per Delete) + per-line push state.
Filters / params:
- **id**: Required — change-request GUID.
Returns: SupplierChangeRequestDto; 404 if not found / not visible (seccode).")]
    public async Task<Result<SupplierChangeRequestDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSupplierChangeRequestByIdQuery(id), ct);
        return Result<SupplierChangeRequestDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "Supplier.ChangeRequest")]
    [EndpointSummary("Raise a supplier change request")]
    [EndpointDescription(@"Supplier raises a post-registration change request (status Draft). Builds one delta line per change; live supplier data is NOT mutated.
Body: CreateSupplierChangeRequestRequest (supplierId, summary, lines[]). Each line: targetEntity (Supplier|Address|Contact|Bank|License), operation (Add|Edit|Delete), targetEntityId? (Edit/Delete), fieldName? + newValue? (Edit), payloadJson? (Add).
Side effects: stamps Owner = supplier G-seccode, requestedBy/At. Authorization: Supplier.ChangeRequest permission + verified SupplierUserMap membership (403 otherwise). 400 on malformed lines.
Returns: SupplierChangeRequestDto. Requires **Supplier.ChangeRequest**.")]
    public async Task<Result<SupplierChangeRequestDto>> Create([FromBody] CreateSupplierChangeRequestRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateSupplierChangeRequestCommand(body), ct);
        return Result<SupplierChangeRequestDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Supplier.ChangeRequest")]
    [EndpointSummary("Edit a draft / bounced-back change request")]
    [EndpointDescription(@"Supplier replaces the line-set + summary. Allowed ONLY while Draft or ChangesRequested (409 otherwise). canWrite-gated (403).
Body: UpdateSupplierChangeRequestRequest (summary, lines[]). Returns SupplierChangeRequestDto; 404 if not found; 400 on malformed lines. Requires **Supplier.ChangeRequest**.")]
    public async Task<Result<SupplierChangeRequestDto>> Update(Guid id, [FromBody] UpdateSupplierChangeRequestRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateSupplierChangeRequestCommand(id, body), ct);
        return Result<SupplierChangeRequestDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = "Supplier.ChangeRequest")]
    [EndpointSummary("Submit a change request for review")]
    [EndpointDescription(@"Supplier submits a Draft / ChangesRequested request for review (→ Submitted). Requires at least one line (400 otherwise). canWrite-gated (403).
Returns: empty success; 404 if not found; 409 if not in a submittable state. Requires **Supplier.ChangeRequest**.")]
    public async Task<Result> Submit(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new SubmitSupplierChangeRequestCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/request-changes")]
    [Authorize(Policy = "Supplier.ApproveChange")]
    [EndpointSummary("Bounce a change request back to the supplier")]
    [EndpointDescription(@"Reviewer requests changes (→ ChangesRequested), bouncing the request back to the supplier for amendment.
Body: RequestChangesRequest (reason — required). Returns empty success; 404 if not found; 409 if not Submitted/UnderReview. Requires **Supplier.ApproveChange**.")]
    public async Task<Result> RequestChanges(Guid id, [FromBody] RequestChangesRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RequestChangesCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "Supplier.ApproveChange")]
    [EndpointSummary("Approve a change request")]
    [EndpointDescription(@"Reviewer approves the request: applies every delta onto the live supplier data (one atomic transaction) then enqueues the per-line ERP push.
Side effects: live supplier/address/contact/bank/license rows mutated; request → Approved then rolled up to Pushed/PartiallyPushed/PushFailed; per-line PushStatus tracked.
Returns: empty success; 404 if not found; 409 if not Submitted/UnderReview OR a concurrent approve won the RowVersion race (skipped). Requires **Supplier.ApproveChange**.")]
    public async Task<Result> Approve(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new ApproveSupplierChangeRequestCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "Supplier.ApproveChange")]
    [EndpointSummary("Reject a change request")]
    [EndpointDescription(@"Reviewer rejects the request (→ Rejected). No deltas applied, nothing pushed to ERP.
Body: RejectSupplierChangeRequest (reason — required). Returns empty success; 404 if not found; 409 if not Submitted/UnderReview. Requires **Supplier.ApproveChange**.")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectSupplierChangeRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectSupplierChangeRequestCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
