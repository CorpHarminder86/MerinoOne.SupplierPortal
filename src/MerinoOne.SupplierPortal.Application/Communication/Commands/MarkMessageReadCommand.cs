using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Communication.Commands;

public record MarkMessageReadCommand(Guid MessageId) : IRequest<Unit>;

public class MarkMessageReadCommandHandler : IRequestHandler<MarkMessageReadCommand, Unit>
{
    private readonly IAppDbContext _db;
    public MarkMessageReadCommandHandler(IAppDbContext db) => _db = db;

    public async Task<Unit> Handle(MarkMessageReadCommand request, CancellationToken ct)
    {
        var msg = await _db.CommunicationMessages.FirstOrDefaultAsync(m => m.Id == request.MessageId, ct)
                  ?? throw new NotFoundException("Message", request.MessageId);
        if (!msg.IsRead)
        {
            msg.IsRead = true;
            msg.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return Unit.Value;
    }
}
