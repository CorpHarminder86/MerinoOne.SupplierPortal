using MediatR;
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
    [EndpointSummary("Create ASN")]
    [EndpointDescription(@"Supplier-submitted ASN for one or more PO lines.
Body:
- **body**: CreateAsnRequest with PO reference + ship lines + carrier metadata.
Side effects:
- Validates dispatched qty against open PO line qty.
- Triggers downstream GR readiness on the buyer side.
Returns: AsnDetailDto on success; 400 on validation; 403 if seccode mismatch. Requires permission **Asn.Write**.")]
    public async Task<Result<AsnDetailDto>> Create([FromBody] CreateAsnRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateAsnCommand(body), ct);
        return Result<AsnDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
