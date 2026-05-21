using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Communication;
using MerinoOne.SupplierPortal.Domain.Entities.Comm;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Communication.Commands;

public record SendMessageCommand(SendMessageRequest Body) : IRequest<MessageDto>;

public class SendMessageCommandValidator : AbstractValidator<SendMessageCommand>
{
    public SendMessageCommandValidator()
    {
        RuleFor(x => x.Body.MessageBody).NotEmpty().MaximumLength(4000);
        RuleFor(x => x).Must(x => x.Body.ThreadId.HasValue || x.Body.ReceiverUserId.HasValue)
            .WithMessage("Either ThreadId or ReceiverUserId is required.");
    }
}

public class SendMessageCommandHandler : IRequestHandler<SendMessageCommand, MessageDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public SendMessageCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public async Task<MessageDto> Handle(SendMessageCommand request, CancellationToken ct)
    {
        if (!_user.IsAuthenticated) throw new ForbiddenException();

        var sender = await _db.AppUsers.FirstOrDefaultAsync(u => u.UserCode == _user.UserCode, ct)
                     ?? throw new NotFoundException("AppUser", _user.UserCode);

        Guid seccodeId;
        Guid threadId = request.Body.ThreadId ?? Guid.NewGuid();

        if (request.Body.ThreadId.HasValue)
        {
            var existing = await _db.CommunicationMessages
                .Where(m => m.ThreadId == request.Body.ThreadId)
                .OrderBy(m => m.SentAt)
                .Select(m => m.SeccodeId)
                .FirstOrDefaultAsync(ct);
            seccodeId = existing != Guid.Empty
                ? existing
                : await ResolveDefaultSeccode(sender.Id, request.Body.ReceiverUserId, ct);
        }
        else
        {
            seccodeId = await ResolveDefaultSeccode(sender.Id, request.Body.ReceiverUserId, ct);
        }

        var msg = new CommunicationMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            PurchaseOrderId = request.Body.PurchaseOrderId,
            SenderUserId = sender.Id,
            ReceiverUserId = request.Body.ReceiverUserId,
            MessageBody = request.Body.MessageBody,
            AttachmentUrl = request.Body.AttachmentUrl,
            SentAt = DateTime.UtcNow,
            IsRead = false,
            IsSystemMessage = false,
            SeccodeId = seccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };

        _db.CommunicationMessages.Add(msg);
        await _db.SaveChangesAsync(ct);

        return new MessageDto(msg.Id, msg.Seq, msg.ThreadId, msg.SenderUserId, sender.FullName,
            msg.ReceiverUserId, msg.MessageBody, msg.AttachmentUrl, msg.SentAt, msg.IsRead, msg.IsSystemMessage);
    }

    private async Task<Guid> ResolveDefaultSeccode(Guid senderUserId, Guid? receiverUserId, CancellationToken ct)
    {
        // pick the sender's U-seccode by default
        var u = await _db.Seccodes.FirstOrDefaultAsync(s => s.AppUserId == senderUserId, ct);
        if (u != null) return u.Id;
        throw new NotFoundException("Seccode", senderUserId);
    }
}
