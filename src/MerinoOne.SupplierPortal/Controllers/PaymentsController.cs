using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Payments.Queries;
using MerinoOne.SupplierPortal.Contracts.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Payments.PaymentListItemDto>;
using ContractsPaymentSummaryPaged = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Payments.PaymentSummaryRowDto>;

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
    [EndpointSummary("Payment list")]
    [EndpointDescription(@"Paged list of payments dispatched (or scheduled) to suppliers.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **supplierId**: Optional — restrict to one supplier.
- **invoiceId**: Optional — restrict to payments against one invoice.
- **search**: Optional — free-text on payment / reference number.
Side effects:
- Seccode-scoped: supplier users see only their own payments.
Returns: PagedResult<PaymentListItemDto>. Requires permission **Payment.Read**.")]
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
    [EndpointSummary("Payment detail")]
    [EndpointDescription(@"Full payment record including allocations and bank details.
Filters / params:
- **id**: Required — payment GUID.
Returns: PaymentDetailDto on success; 404 if not found; 403 if seccode mismatch. Requires permission **Payment.Read**.")]
    public async Task<Result<PaymentDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetPaymentByIdQuery(id), ct);
        return Result<PaymentDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("summary")]
    [Authorize(Policy = "Payment.Read")]
    [EndpointSummary("Payment summary")]
    [EndpointDescription(@"Enhancement R4 — Module 7. Paged invoice-centric payment summary (one row per invoice).
Columns: InvoiceNumber, InvoiceDate, InvoiceAmount (NetAmount), GrnNumber, GrnDate, GrnCount, IssueReported,
PaymentDueDate, PaymentReference, ReceivedAmount (Σ Payment.NetPaid), BalanceToReceive (NetAmount − Received).
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50, max 200).
- **supplierId**: Optional — restrict to one supplier.
- **from / to**: Optional — invoice-date range.
- **status**: Optional — InvoiceStatus filter.
Side effects:
- Admin / Manager take a no-seccode Dapper join (all suppliers). Supplier users are HARD-GATED onto the
  EF path where the global seccode + company filters scope rows to their own invoices — a supplier can never
  reach the privileged query.
Returns: PagedResult<PaymentSummaryRowDto>. Requires permission **Payment.Read**.")]
    public async Task<Result<ContractsPaymentSummaryPaged>> Summary(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetPaymentSummaryQuery(page, pageSize, supplierId, from, to, status), ct);
        return Result<ContractsPaymentSummaryPaged>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}/remittance")]
    [Authorize(Policy = "Payment.Read")]
    [EndpointSummary("Payment remittance")]
    [EndpointDescription(@"Remittance advice for a single payment — printable summary of invoice allocations.
Filters / params:
- **id**: Required — payment GUID.
Returns: RemittanceDto on success; 404 if not found; 403 if seccode mismatch. Requires permission **Payment.Read**.")]
    public async Task<Result<RemittanceDto>> Remittance(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetRemittanceQuery(id), ct);
        return Result<RemittanceDto>.Ok(data, HttpContext.TraceIdentifier);
    }
}
