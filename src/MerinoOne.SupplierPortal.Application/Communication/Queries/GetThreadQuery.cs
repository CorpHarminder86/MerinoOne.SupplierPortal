using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Communication;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Communication.Queries;

public record GetThreadQuery(Guid ThreadId) : IRequest<List<MessageDto>>;

public class GetThreadQueryHandler : IRequestHandler<GetThreadQuery, List<MessageDto>>
{
    private readonly IAppDbContext _db;
    public GetThreadQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<MessageDto>> Handle(GetThreadQuery request, CancellationToken ct)
    {
        var query = from m in _db.CommunicationMessages
                    join u in _db.AppUsers on m.SenderUserId equals u.Id
                    where m.ThreadId == request.ThreadId
                    orderby m.SentAt
                    select new MessageDto(m.Id, m.Seq, m.ThreadId, m.SenderUserId, u.FullName,
                        m.ReceiverUserId, m.MessageBody, m.AttachmentUrl, m.SentAt, m.IsRead, m.IsSystemMessage);

        return await query.ToListAsync(ct);
    }
}

public record GetThreadListQuery() : IRequest<List<ThreadSummaryDto>>;

public class GetThreadListQueryHandler : IRequestHandler<GetThreadListQuery, List<ThreadSummaryDto>>
{
    private readonly IAppDbContext _db;
    public GetThreadListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<ThreadSummaryDto>> Handle(GetThreadListQuery request, CancellationToken ct)
    {
        var threads = await _db.CommunicationMessages
            .GroupBy(m => m.ThreadId)
            .Select(g => new
            {
                ThreadId = g.Key,
                MessageCount = g.Count(),
                UnreadCount = g.Count(m => !m.IsRead),
                LastMessageAt = g.Max(m => m.SentAt),
                LastSenderId = g.OrderByDescending(m => m.SentAt).Select(m => m.SenderUserId).FirstOrDefault(),
                LastSnippet = g.OrderByDescending(m => m.SentAt).Select(m => m.MessageBody).FirstOrDefault() ?? string.Empty
            })
            .OrderByDescending(t => t.LastMessageAt)
            .ToListAsync(ct);

        var senderIds = threads.Select(t => t.LastSenderId).Distinct().ToList();
        var senders = await _db.AppUsers.Where(u => senderIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        return threads.Select(t => new ThreadSummaryDto(
            t.ThreadId,
            t.LastSnippet.Length > 50 ? t.LastSnippet[..50] : t.LastSnippet,
            t.MessageCount,
            t.UnreadCount,
            t.LastMessageAt,
            senders.GetValueOrDefault(t.LastSenderId, "—"),
            t.LastSnippet.Length > 100 ? t.LastSnippet[..100] + "…" : t.LastSnippet
        )).ToList();
    }
}
