using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Ln.Monitor;

// ── R9 — outbox monitor: Skipped rows visible with reason + gateVersion (a skip is a decision — this
// screen is its ONLY surface, D-R9-9), permanent-failure badge, and the audited manual re-arm. ─────────

public record GetOutboxMessagesQuery(string? Status, string? TransactionType, int Page = 1, int PageSize = 50)
    : IRequest<OutboxMessagePageDto>;

public class GetOutboxMessagesQueryHandler : IRequestHandler<GetOutboxMessagesQuery, OutboxMessagePageDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetOutboxMessagesQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<OutboxMessagePageDto> Handle(GetOutboxMessagesQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var query = _db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tid && !m.IsDeleted);
        if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<OutboxStatus>(request.Status, out var status))
            query = query.Where(m => m.Status == status);
        if (!string.IsNullOrWhiteSpace(request.TransactionType))
            query = query.Where(m => m.TransactionType == request.TransactionType);

        var total = await query.CountAsync(ct);
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 200);
        var rows = await query
            .OrderByDescending(m => m.CreatedOn)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(m => new OutboxMessageDto(m.Id, m.TransactionType, m.EntityName, m.EntityId, m.DeterministicKey,
                m.Status.ToString(), m.AttemptCount, m.GateVersion, m.SkipReason, m.ErrorClass, m.LastError,
                m.CreatedOn, m.DispatchedAt, m.AckedAt))
            .ToListAsync(ct);
        return new OutboxMessagePageDto(total, rows);
    }
}

/// <summary>
/// Manual re-arm (Skipped/Failed → Pending). Allowed on Permanent-classified failures too — admin
/// override with a UI warning (D-R9-5: the classification informs, it does not imprison).
/// </summary>
public record RearmOutboxMessageCommand(Guid OutboxMessageId) : IRequest<RearmOutboxResultDto>;

public class RearmOutboxMessageCommandHandler : IRequestHandler<RearmOutboxMessageCommand, RearmOutboxResultDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public RearmOutboxMessageCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<RearmOutboxResultDto> Handle(RearmOutboxMessageCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");
        var row = await _db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.OutboxMessageId && m.TenantId == tid && !m.IsDeleted, ct);
        if (row is null) return new RearmOutboxResultDto(false, false, "Row not found.");
        if (row.Status is not (OutboxStatus.Skipped or OutboxStatus.Failed))
            return new RearmOutboxResultDto(false, false, $"Row is {row.Status} — only Skipped/Failed rows re-arm.");

        var wasPermanent = row.ErrorClass == "Permanent";
        var now = DateTime.UtcNow;
        var affected = await _db.OutboxMessages.IgnoreQueryFilters()
            .Where(m => m.Id == row.Id && (m.Status == OutboxStatus.Skipped || m.Status == OutboxStatus.Failed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, OutboxStatus.Pending)
                .SetProperty(m => m.LastError, (string?)null)
                .SetProperty(m => m.SkipReason, (string?)null)
                .SetProperty(m => m.ErrorClass, (string?)null)
                .SetProperty(m => m.DispatchedAt, (DateTime?)null)
                .SetProperty(m => m.UpdatedBy, _user.UserCode)
                .SetProperty(m => m.UpdatedOn, now), ct);

        return affected == 1
            ? new RearmOutboxResultDto(true, wasPermanent,
                wasPermanent ? "Re-armed a PERMANENT-classified failure — fix the config/mapping first or it will fail again." : null)
            : new RearmOutboxResultDto(false, wasPermanent, "The row changed state concurrently — reload.");
    }
}
