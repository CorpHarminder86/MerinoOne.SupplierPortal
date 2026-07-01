using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Shipments.GoodsReceiptDto>;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/goods-receipts")]
public class GoodsReceiptsController : ControllerBase
{
    private readonly IMediator _mediator;
    public GoodsReceiptsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.GoodsReceiptRead)]
    [EndpointSummary("Goods receipt list")]
    [EndpointDescription(@"Paged list of buyer-side goods receipts (read-only for suppliers).
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **purchaseOrderId**: Optional — restrict to one PO.
- **asnId**: Optional — restrict to one ASN.
- **search**: Optional — free-text on receipt number / reference.
Side effects:
- Seccode-scoped: supplier users see only receipts against their own POs.
Returns: PagedResult<GoodsReceiptDto>. Requires permission **GoodsReceipt.Read**.")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? purchaseOrderId = null,
        [FromQuery] Guid? asnId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetGoodsReceiptListQuery(page, pageSize, purchaseOrderId, asnId, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }
}
