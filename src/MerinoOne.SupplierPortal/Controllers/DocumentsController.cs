using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Documents.Queries;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// 2026-07-04 — the cross-module document register (read-only). One RLS-scoped list over every
/// <c>doc.DocumentUpload</c>, complementing the per-entity attachment panels and the IDM sync log.
/// </summary>
[ApiController]
[Authorize]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private readonly IMediator _mediator;
    public DocumentsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.DocumentRead)]
    [EndpointSummary("Document register")]
    [EndpointDescription(@"Paged, RLS-scoped list of every uploaded document the caller may see (admin: whole tenant; supplier: own seccode rows).
Filters / params:
- **page** / **pageSize**: paging (default 1 / 50, max 200).
- **ownerEntityType**: Asn | Invoice | Supplier | SupplierLicense | Staging | PendingInvite.
- **documentType**: exact attachment-type code (e.g. Invoice, AsnAttachment, Msme).
- **fileName**: filename contains.
- **fromDate** / **toDate**: inclusive createdOn range.
- **idmStatus**: Synced (pid present) | NotSynced.
- **supplierId**: restrict to documents owned (directly or via Asn/Invoice/SupplierLicense) by one supplier.
Returns: PagedResult<DocumentListItemDto> with the owner handle + Infor IDM sync state per row. Requires permission **Document.Read**.")]
    public async Task<Result<PagedResult<DocumentListItemDto>>> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? ownerEntityType = null,
        [FromQuery] string? documentType = null, [FromQuery] string? fileName = null,
        [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] string? idmStatus = null, [FromQuery] Guid? supplierId = null, CancellationToken ct = default)
    {
        var data = await _mediator.Send(
            new GetDocumentsQuery(page, pageSize, ownerEntityType, documentType, fileName, fromDate, toDate, idmStatus, supplierId), ct);
        return Result<PagedResult<DocumentListItemDto>>.Ok(data, HttpContext.TraceIdentifier);
    }
}
