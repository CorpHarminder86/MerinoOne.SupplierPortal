using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Switches;

// ── R9 (TSD R9 §2.6, D-R9-11) — the two DB-backed kill-switch scopes: OutboundGlobal (dispatcher drains
// nothing for the tenant; enqueue continues) and InboundErpAck (accept-and-hold; replay on re-enable).
// Every toggle is audited with a MANDATORY reason. Absent row = enabled; rows lazy-create on first toggle. ──

public record GetIntegrationSwitchesQuery : IRequest<IReadOnlyList<IntegrationSwitchDto>>;

public class GetIntegrationSwitchesQueryHandler : IRequestHandler<GetIntegrationSwitchesQuery, IReadOnlyList<IntegrationSwitchDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetIntegrationSwitchesQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<IReadOnlyList<IntegrationSwitchDto>> Handle(GetIntegrationSwitchesQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var rows = await _db.IntegrationSwitches.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tid && !s.IsDeleted)
            .ToListAsync(ct);
        var heldCount = await _db.HeldInboundMessages.IgnoreQueryFilters()
            .CountAsync(h => h.TenantId == tid && h.Status == "Held" && !h.IsDeleted, ct);

        IntegrationSwitchDto Project(string scope)
        {
            var row = rows.FirstOrDefault(r => r.Scope == scope);
            return new IntegrationSwitchDto(
                scope,
                row?.IsEnabled ?? true,   // absent row = enabled
                row?.LastReason,
                row?.UpdatedBy ?? row?.CreatedBy,
                row?.UpdatedOn ?? row?.CreatedOn,
                scope == IntegrationSwitchScope.InboundErpAck ? heldCount : 0);
        }

        return new[] { Project(IntegrationSwitchScope.OutboundGlobal), Project(IntegrationSwitchScope.InboundErpAck) };
    }
}

public record ToggleIntegrationSwitchCommand(string Scope, ToggleIntegrationSwitchRequest Body) : IRequest<bool>;

public class ToggleIntegrationSwitchValidator : AbstractValidator<ToggleIntegrationSwitchCommand>
{
    public ToggleIntegrationSwitchValidator()
    {
        RuleFor(x => x.Scope).Must(s => s is IntegrationSwitchScope.OutboundGlobal or IntegrationSwitchScope.InboundErpAck)
            .WithMessage($"Scope must be {IntegrationSwitchScope.OutboundGlobal} or {IntegrationSwitchScope.InboundErpAck}.");
        RuleFor(x => x.Body.Reason).NotEmpty().MinimumLength(5).MaximumLength(500)
            .WithMessage("A reason note (5–500 chars) is mandatory on every kill-switch toggle.");
    }
}

public class ToggleIntegrationSwitchCommandHandler : IRequestHandler<ToggleIntegrationSwitchCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public ToggleIntegrationSwitchCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<bool> Handle(ToggleIntegrationSwitchCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");
        var now = DateTime.UtcNow;

        var row = await _db.IntegrationSwitches.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tid && s.Scope == request.Scope && !s.IsDeleted, ct);
        var oldEnabled = row?.IsEnabled ?? true;

        if (row is null)
        {
            row = new IntegrationSwitch
            {
                TenantId = tid,
                Scope = request.Scope,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            };
            _db.IntegrationSwitches.Add(row);
        }
        row.IsEnabled = request.Body.Enable;
        row.LastReason = request.Body.Reason.Trim();
        row.UpdatedBy = _user.UserCode;
        row.UpdatedOn = now;

        _db.IntegrationSwitchAudits.Add(new IntegrationSwitchAudit
        {
            TenantId = tid,
            IntegrationSwitch = row,
            Scope = request.Scope,
            OldEnabled = oldEnabled,
            NewEnabled = request.Body.Enable,
            Reason = row.LastReason,
            CreatedBy = _user.UserCode,
            CreatedOn = now,
        });

        await _db.SaveChangesAsync(ct);
        return true;
    }
}

public record GetIntegrationSwitchAuditQuery(string? Scope) : IRequest<IReadOnlyList<IntegrationSwitchAuditDto>>;

public class GetIntegrationSwitchAuditQueryHandler : IRequestHandler<GetIntegrationSwitchAuditQuery, IReadOnlyList<IntegrationSwitchAuditDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetIntegrationSwitchAuditQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<IReadOnlyList<IntegrationSwitchAuditDto>> Handle(GetIntegrationSwitchAuditQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        return await _db.IntegrationSwitchAudits.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.TenantId == tid && !a.IsDeleted && (request.Scope == null || a.Scope == request.Scope))
            .OrderByDescending(a => a.CreatedOn)
            .Take(50)
            .Select(a => new IntegrationSwitchAuditDto(a.Scope, a.OldEnabled, a.NewEnabled, a.Reason, a.CreatedBy, a.CreatedOn))
            .ToListAsync(ct);
    }
}

public record GetHeldInboundMessagesQuery(string? Status) : IRequest<IReadOnlyList<HeldInboundMessageDto>>;

public class GetHeldInboundMessagesQueryHandler : IRequestHandler<GetHeldInboundMessagesQuery, IReadOnlyList<HeldInboundMessageDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetHeldInboundMessagesQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<IReadOnlyList<HeldInboundMessageDto>> Handle(GetHeldInboundMessagesQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        return await _db.HeldInboundMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(h => h.TenantId == tid && !h.IsDeleted && (request.Status == null || h.Status == request.Status))
            .OrderByDescending(h => h.CreatedOn)
            .Take(100)
            .Select(h => new HeldInboundMessageDto(h.Id, h.EndpointName, h.Status, h.ReplayAttempts, h.LastError, h.CreatedOn, h.ReplayedOn))
            .ToListAsync(ct);
    }
}
