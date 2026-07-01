using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Shipments.Queries;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// R5 (TSD R5 Addendum §8) — Delivery Schedule grid. Schedules are PORTAL-CREATED when a PO becomes shippable
/// (§8.1) and upserted on a material Modify (§8.2); there is NO manual propose/approve in R5 (the pre-R5 endpoints
/// are retired). This controller exposes only the read grid — the ASN-creation surface (§7).
/// </summary>
[ApiController]
[Authorize]
[Route("api/delivery-schedules")]
public class DeliverySchedulesController : ControllerBase
{
    private readonly IMediator _mediator;
    public DeliverySchedulesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.DeliveryScheduleRead)]
    [EndpointSummary("Delivery schedule grid")]
    [EndpointDescription(@"R5 — Delivery Schedule grid (the ASN-creation surface). One row per PO line, sorted PO → Line → Delivery date ASC, with remaining-to-ship derived from the R4 line balance (orderQty − shippedQtyToDate).
Filters / params (all optional):
- **page**: 1-based page index (default 1).
- **pageSize**: rows per page (default 50, max 500).
- **supplierId**: restrict to one supplier.
- **shipToAddressId**: restrict to one ship-to address.
- **purchaseOrderId**: restrict to one PO.
- **deliveryDateFrom / deliveryDateTo**: inclusive delivery-date day range.
- **status**: schedule status (Approved).
Returns: DeliveryScheduleGridDto — the paged rows plus the auto-hide ship-to signal (DistinctShipToCount + ShowShipToFilter; the Ship-To filter is hidden when only one ship-to is present). Requires permission **DeliverySchedule.Read**.")]
    public async Task<Result<DeliveryScheduleGridDto>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] Guid? shipToAddressId = null,
        [FromQuery] Guid? purchaseOrderId = null,
        [FromQuery] DateTime? deliveryDateFrom = null,
        [FromQuery] DateTime? deliveryDateTo = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var filter = new DeliveryScheduleFilterRequest(
            page, pageSize, supplierId, shipToAddressId, purchaseOrderId, deliveryDateFrom, deliveryDateTo, status);
        var data = await _mediator.Send(new GetDeliveryScheduleListQuery(filter), ct);
        return Result<DeliveryScheduleGridDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
