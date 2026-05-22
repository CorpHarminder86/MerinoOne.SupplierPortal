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
    public async Task<Result<CreditDebitNoteDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetCreditDebitNoteByIdQuery(id), ct);
        return Result<CreditDebitNoteDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "CreditDebitNote.Write")]
    public async Task<Result<CreditDebitNoteDetailDto>> Create([FromBody] CreateCreditDebitNoteRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateCreditDebitNoteCommand(body), ct);
        return Result<CreditDebitNoteDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = "CreditDebitNote.Approve")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveCreditDebitNoteRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveCreditDebitNoteCommand(id, body ?? new ApproveCreditDebitNoteRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
