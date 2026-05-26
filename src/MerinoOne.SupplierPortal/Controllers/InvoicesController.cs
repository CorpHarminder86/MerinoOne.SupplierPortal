using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Invoices.Commands;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Invoices.InvoiceListItemDto>;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/invoices")]
public class InvoicesController : ControllerBase
{
    private readonly IMediator _mediator;
    public InvoicesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Invoice.Read")]
    [EndpointSummary("Invoice list")]
    [EndpointDescription(@"Paged list of supplier invoices visible to the caller.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **status**: Optional — invoice lifecycle status (Submitted / Reviewed / Approved / Rejected / Paid).
- **supplierId**: Optional — restrict to one supplier.
- **purchaseOrderId**: Optional — restrict to one PO.
- **search**: Optional — free-text on invoice number / reference.
Side effects:
- Seccode-scoped: non-privileged users see only their suppliers' invoices.
Returns: PagedResult<InvoiceListItemDto>. Requires permission **Invoice.Read**.")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] Guid? purchaseOrderId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetInvoiceListQuery(page, pageSize, status, supplierId, purchaseOrderId, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Invoice.Read")]
    [EndpointSummary("Invoice detail")]
    [EndpointDescription(@"Full invoice header + line items + linked PO / GR references + attachments.
Filters / params:
- **id**: Required — invoice GUID.
Returns: InvoiceDetailDto on success; 404 if not found; 403 if seccode mismatch. Requires permission **Invoice.Read**.")]
    public async Task<Result<InvoiceDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInvoiceByIdQuery(id), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "Invoice.Submit")]
    [EndpointSummary("Submit invoice")]
    [EndpointDescription(@"Supplier submits a new invoice against one or more POs.
Body:
- **body**: CreateInvoiceRequest with PO references, line items, tax breakdown, attachments.
Side effects:
- Creates the invoice in Submitted status and queues it for buyer review.
- Triggers MockDocumentValidationService to extract / validate fields asynchronously.
Returns: InvoiceDetailDto on success; 400 on validation; 403 if seccode mismatch. Requires permission **Invoice.Submit**.")]
    public async Task<Result<InvoiceDetailDto>> Create([FromBody] CreateInvoiceRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateInvoiceCommand(body), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/review")]
    [Authorize(Policy = "Invoice.Review")]
    [EndpointSummary("Review invoice")]
    [EndpointDescription(@"Buyer reviewer flags an invoice as triaged before sending it for approval.
Filters / params:
- **id**: Required — invoice GUID.
- **body**: Optional — ReviewInvoiceRequest with reviewer notes.
Side effects:
- Flips status to Reviewed + stamps reviewer/timestamp.
Returns: empty success; 404 if not found; 409 if not in reviewable state. Requires permission **Invoice.Review**.")]
    public async Task<Result> Review(Guid id, [FromBody] ReviewInvoiceRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ReviewInvoiceCommand(id, body ?? new ReviewInvoiceRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "Invoice.Approve")]
    [EndpointSummary("Approve invoice")]
    [EndpointDescription(@"Finance approves a reviewed invoice for payment.
Filters / params:
- **id**: Required — invoice GUID.
- **body**: Optional — ApproveInvoiceRequest with approver notes.
Side effects:
- Flips status to Approved + stamps approver/timestamp.
- Becomes eligible for the next payment run.
Returns: empty success; 404 if not found; 409 if not in approvable state. Requires permission **Invoice.Approve**.")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveInvoiceRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveInvoiceCommand(id, body ?? new ApproveInvoiceRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "Invoice.Approve")]
    [EndpointSummary("Reject invoice")]
    [EndpointDescription(@"Finance rejects an invoice; supplier must amend + resubmit.
Filters / params:
- **id**: Required — invoice GUID.
- **body**: RejectInvoiceRequest with required reason.
Side effects:
- Flips status to Rejected + records reason + timestamp.
- Notifies the supplier via the configured email template.
Returns: empty success; 404 if not found; 409 if not in rejectable state. Requires permission **Invoice.Approve**.")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectInvoiceRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectInvoiceCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
