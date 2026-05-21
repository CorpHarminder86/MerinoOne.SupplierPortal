using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Communication.Commands;
using MerinoOne.SupplierPortal.Application.Communication.Queries;
using MerinoOne.SupplierPortal.Contracts.Communication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class MessagesController : ControllerBase
{
    private readonly IMediator _mediator;
    public MessagesController(IMediator mediator) => _mediator = mediator;

    [HttpGet("threads")]
    public async Task<Result<List<ThreadSummaryDto>>> Threads(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetThreadListQuery(), ct);
        return Result<List<ThreadSummaryDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("threads/{threadId:guid}")]
    public async Task<Result<List<MessageDto>>> Thread(Guid threadId, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetThreadQuery(threadId), ct);
        return Result<List<MessageDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("messages")]
    public async Task<Result<MessageDto>> Send([FromBody] SendMessageRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new SendMessageCommand(body), ct);
        return Result<MessageDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("messages/{id:guid}/read")]
    public async Task<Result> MarkRead(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new MarkMessageReadCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
