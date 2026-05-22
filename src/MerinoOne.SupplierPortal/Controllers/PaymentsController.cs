using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Payments.Queries;
using MerinoOne.SupplierPortal.Contracts.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Payments.PaymentListItemDto>;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IMediator _mediator;
    public PaymentsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Payment.Read")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] Guid? invoiceId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetPaymentListQuery(page, pageSize, supplierId, invoiceId, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Payment.Read")]
    public async Task<Result<PaymentDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPaymentByIdQuery(id), ct);
        return Result<PaymentDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}/remittance")]
    [Authorize(Policy = "Payment.Read")]
    public async Task<Result<RemittanceDto>> Remittance(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetRemittanceQuery(id), ct);
        return Result<RemittanceDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
