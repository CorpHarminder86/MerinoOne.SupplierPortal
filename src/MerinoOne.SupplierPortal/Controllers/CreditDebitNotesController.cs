using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Invoices.Commands;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Invoices.CreditDebitNoteListItemDto>;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/credit-debit-notes")]
public class CreditDebitNotesController : ControllerBase
{
    private readonly IMediator _mediator;
    public CreditDebitNotesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "CreditDebitNote.Read")]
    [EndpointSummary("Credit/debit note list")]
    [EndpointDescription(@"Paged list of credit + debit notes against supplier invoices.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **status**: Optional — note lifecycle status filter.
- **invoiceId**: Optional — restrict to one invoice.
- **noteType**: Optional — ""Credit"" or ""Debit"".
- **search**: Optional — free-text on note number / reference.
Returns: PagedResult<CreditDebitNoteListItemDto>. Requires permission **CreditDebitNote.Read**.")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] Guid? invoiceId = null,
        [FromQuery] string? noteType = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetCreditDebitNoteListQuery(page, pageSize, status, invoiceId, noteType, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "CreditDebitNote.Read")]
    [EndpointSummary("Credit/debit note detail")]
    [EndpointDescription(@"Full credit/debit note header + line items + linked invoice reference.
Filters / params:
- **id**: Required — note GUID.
Returns: CreditDebitNoteDetailDto on success; 404 if not found; 403 if seccode mismatch. Requires permission **CreditDebitNote.Read**.")]
    public async Task<Result<CreditDebitNoteDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetCreditDebitNoteByIdQuery(id), ct);
        return Result<CreditDebitNoteDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "CreditDebitNote.Write")]
    [EndpointSummary("Create credit/debit note")]
    [EndpointDescription(@"Supplier-submitted credit or debit note against an existing invoice.
Body:
- **body**: CreateCreditDebitNoteRequest with invoice reference, note type, line items + amounts.
Side effects:
- Creates the note in pending-approval status.
Returns: CreditDebitNoteDetailDto on success; 400 on validation; 403 if seccode mismatch. Requires permission **CreditDebitNote.Write**.")]
    public async Task<Result<CreditDebitNoteDetailDto>> Create([FromBody] CreateCreditDebitNoteRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateCreditDebitNoteCommand(body), ct);
        return Result<CreditDebitNoteDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "CreditDebitNote.Approve")]
    [EndpointSummary("Approve credit/debit note")]
    [EndpointDescription(@"Approves a pending credit/debit note, flipping status to Approved.
Filters / params:
- **id**: Required — note GUID.
- **body**: Optional — ApproveCreditDebitNoteRequest with reviewer notes.
Side effects:
- Flips status to Approved + stamps approver/timestamp.
- May offset the linked invoice's outstanding balance downstream.
Returns: empty success; 404 if not found; 409 if not in approvable state. Requires permission **CreditDebitNote.Approve**.")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveCreditDebitNoteRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveCreditDebitNoteCommand(id, body ?? new ApproveCreditDebitNoteRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
