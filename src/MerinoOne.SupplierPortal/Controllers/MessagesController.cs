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
    [EndpointSummary("Message thread list")]
    [EndpointDescription(@"Lists all message threads the current user participates in.
Side effects:
- Seccode-scoped: supplier users see only threads linked to their suppliers; internal users see threads in their domain.
Returns: List<ThreadSummaryDto> ordered by most recent activity.")]
    public async Task<Result<List<ThreadSummaryDto>>> Threads(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetThreadListQuery(), ct);
        return Result<List<ThreadSummaryDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("threads/{threadId:guid}")]
    [EndpointSummary("Thread messages")]
    [EndpointDescription(@"Returns the full ordered message list for a single thread.
Filters / params:
- **threadId**: Required — thread GUID.
Returns: List<MessageDto> ordered by SentAt asc; 404 if thread not found; 403 if caller is not a participant.")]
    public async Task<Result<List<MessageDto>>> Thread(Guid threadId, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetThreadQuery(threadId), ct);
        return Result<List<MessageDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("messages/recipients")]
    [EndpointSummary("Message recipients")]
    [EndpointDescription(@"Users the caller may start a new message with (compose picker).
- Supplier users see internal staff (Buyer/Finance/Admin); internal users see all other active users in the tenant.
Returns: List<MessageRecipientDto>, tenant-scoped.")]
    public async Task<Result<List<MessageRecipientDto>>> Recipients(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetMessageRecipientsQuery(), ct);
        return Result<List<MessageRecipientDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("messages")]
    [EndpointSummary("Send message")]
    [EndpointDescription(@"Posts a new message into an existing thread, or starts a new thread.
Body:
- **body**: SendMessageRequest with thread/contextual reference, body text, optional attachments.
Side effects:
- Triggers email/in-app notification to thread recipients.
Returns: MessageDto on success; 400 on validation; 403 if caller is not a participant.")]
    public async Task<Result<MessageDto>> Send([FromBody] SendMessageRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new SendMessageCommand(body), ct);
        return Result<MessageDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("messages/{id:guid}/read")]
    [EndpointSummary("Mark message read")]
    [EndpointDescription(@"Flags a single message as read by the current user.
Filters / params:
- **id**: Required — message GUID.
Side effects:
- Stamps ReadAt for the caller; does not affect other recipients.
Returns: empty success; 404 if not found.")]
    public async Task<Result> MarkRead(Guid id, CancellationToken ct)
    {
        await _mediator.Send(new MarkMessageReadCommand(id), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
