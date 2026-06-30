using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Documents.Queries;
using MerinoOne.SupplierPortal.Contracts.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R5 (TSD R5 Addendum §13, Component 9) — the Policy-Driven Attachment Panel READ-MODEL endpoint. Thin: it only
/// <c>MediatR.Send</c>s <see cref="GetAttachmentPanelQuery"/> and maps the result. READ-ONLY — uploads / removes /
/// downloads go through <c>DocumentUploadsController</c> (POST /api/document-uploads/attach,
/// DELETE /api/document-uploads/{id}, GET files/proxy/{id}). No enforcement lives here (§13.6).
/// </summary>
[ApiController]
[Authorize]
[Route("api/attachments")]
public class AttachmentsController : ControllerBase
{
    private readonly IMediator _mediator;
    public AttachmentsController(IMediator mediator) => _mediator = mediator;

    /// <summary>
    /// R5 (§13.8) — returns the panel slot descriptors for an (entity, instance): one slot per ACTIVE attachment
    /// policy type, each with its effective requirement badge and ALL its uploaded files (§13.4). An EMPTY list ⇒
    /// no active policy ⇒ the host renders no panel (§13.2). Slots are ordered Mandatory → Warning → Optional,
    /// then alphabetical by type name (§13.3). The same query serves <c>entity=Asn</c>, <c>Invoice</c> and
    /// <c>Supplier</c> (§13.7). The supplier-override (D5) tier is resolved from context: <c>Supplier</c> ⇒ the id
    /// IS the supplier; <c>Asn</c>/<c>Invoice</c> ⇒ the owning supplier.
    ///
    /// <para>Auth: any authenticated principal. Row visibility of the files is seccode-scoped (a supplier sees its
    /// own; admin/buyer read via their access path), so the descriptor only ever reflects what the caller may see.
    /// READ-ONLY and purely descriptive — enforcement stays at the host's submit site (R4 AttachmentSubmitGuard).</para>
    /// </summary>
    [HttpGet("panel")]
    [EndpointSummary("Attachment panel read-model (slots from policy)")]
    [EndpointDescription(@"R5 §13.8 — policy-driven Attachment Panel read-model. Returns one slot per ACTIVE
attachment policy type for (entity, id), each with its effective requirement badge (Mandatory/Warning/Optional)
and ALL its uploaded files (multiple files per slot). EMPTY array ⇒ no active policy ⇒ no panel (the
'no policy → no control' rule). Slots ordered Mandatory → Warning → Optional, then alphabetical by name. Same
query for entity=Asn | Invoice | Supplier. READ-ONLY — upload/remove/download via /api/document-uploads. Any
authenticated caller; file visibility is seccode-scoped.")]
    public async Task<Result<List<AttachmentPanelSlotDto>>> Panel(
        [FromQuery] string entity,
        [FromQuery] Guid id,
        CancellationToken ct)
    {
        var slots = await _mediator.Send(new GetAttachmentPanelQuery(entity, id), ct);
        return Result<List<AttachmentPanelSlotDto>>.Ok(slots.ToList(), HttpContext.TraceIdentifier);
    }
}
