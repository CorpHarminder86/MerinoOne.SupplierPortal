using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Security;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Connection;

// R10 — connection points: named outbound connection targets. One default per tenant (seeded
// "Default — Infor ION"). InforION rows carry no URL/auth (single source of truth stays the tenant's
// Infor connection settings); other system types own theirs here, auth encrypted via ISettingProtector.

/// <summary>System types with a registered outbound transport TODAY. A connection of any other type can be
/// saved (declared ahead of its transport) but cannot be assigned to a config row until the transport ships.</summary>
public static class OutboundTransportCatalog
{
    public static readonly IReadOnlySet<string> Available = new HashSet<string>(StringComparer.Ordinal)
    {
        ConnectionSystemTypes.InforIon,
    };
}

public record GetConnectionPointsQuery : IRequest<IReadOnlyList<ConnectionPointDto>>;

public class GetConnectionPointsQueryHandler : IRequestHandler<GetConnectionPointsQuery, IReadOnlyList<ConnectionPointDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public GetConnectionPointsQueryHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<IReadOnlyList<ConnectionPointDto>> Handle(GetConnectionPointsQuery request, CancellationToken ct)
    {
        var tid = _user.TenantId;
        var rows = await _db.ConnectionPoints.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tid && !p.IsDeleted)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name)
            .ToListAsync(ct);

        // In-use counts as one grouped query — a correlated Count subquery with IgnoreQueryFilters inside
        // the projection does not translate (500 at runtime).
        var counts = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.TenantId == tid && c.ConnectionPointId != null && !c.IsDeleted)
            .GroupBy(c => c.ConnectionPointId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return rows.Select(p => new ConnectionPointDto(
                p.Id, p.Seq, p.Name, p.SystemType, p.BaseUrl, p.IsDefault, p.Notes,
                p.AuthConfigJson != null,
                counts.GetValueOrDefault(p.Id),
                OutboundTransportCatalog.Available.Contains(p.SystemType),
                p.CreatedOn, p.UpdatedOn))
            .ToList();
    }
}

public record SaveConnectionPointCommand(SaveConnectionPointRequest Body) : IRequest<Guid>;

public class SaveConnectionPointValidator : AbstractValidator<SaveConnectionPointCommand>
{
    public SaveConnectionPointValidator()
    {
        RuleFor(x => x.Body.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Body.SystemType).NotEmpty()
            .Must(t => ConnectionSystemTypes.All.Contains(t))
            .WithMessage($"System type must be one of: {string.Join(", ", ConnectionSystemTypes.All)}.");
        RuleFor(x => x.Body.BaseUrl).MaximumLength(400);
        RuleFor(x => x.Body.Notes).MaximumLength(500);
    }
}

public class SaveConnectionPointCommandHandler : IRequestHandler<SaveConnectionPointCommand, Guid>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISettingProtector _protector;
    public SaveConnectionPointCommandHandler(IAppDbContext db, ICurrentUser user, ISettingProtector protector)
    { _db = db; _user = user; _protector = protector; }

    public async Task<Guid> Handle(SaveConnectionPointCommand request, CancellationToken ct)
    {
        var b = request.Body;
        if (_user.TenantId is not { } tid) throw new ValidationException("No tenant context.");

        if (b.SystemType == ConnectionSystemTypes.InforIon)
        {
            // ION credentials/URL live on Settings → Infor CloudSuite — one source of truth.
            if (!string.IsNullOrWhiteSpace(b.BaseUrl) || !string.IsNullOrWhiteSpace(b.AuthConfigJson))
                throw new ValidationException("InforION connections resolve base URL + auth from the Infor connection settings — leave both blank here.");
        }
        else if (string.IsNullOrWhiteSpace(b.BaseUrl))
        {
            throw new ValidationException($"A base URL is required for a {b.SystemType} connection.");
        }

        var row = b.Id is { } id
            ? await _db.ConnectionPoints.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tid && !p.IsDeleted, ct)
            : null;

        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new ConnectionPoint { TenantId = tid, CreatedBy = _user.UserCode, CreatedOn = now };
            _db.ConnectionPoints.Add(row);
        }
        else
        {
            if (row.SystemType != b.SystemType && await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
                    .AnyAsync(c => c.ConnectionPointId == row.Id && !c.IsDeleted, ct))
                throw new ValidationException("System type cannot change while integration configs are tagged to this connection.");
            row.UpdatedBy = _user.UserCode;
            row.UpdatedOn = now;
        }

        row.Name = b.Name.Trim();
        row.SystemType = b.SystemType;
        row.BaseUrl = string.IsNullOrWhiteSpace(b.BaseUrl) ? null : b.BaseUrl.Trim();
        row.Notes = string.IsNullOrWhiteSpace(b.Notes) ? null : b.Notes.Trim();
        // Null request auth = keep the stored blob; a supplied value is encrypted at rest.
        if (b.AuthConfigJson is not null)
            row.AuthConfigJson = string.IsNullOrWhiteSpace(b.AuthConfigJson) ? null : _protector.Protect(b.AuthConfigJson);

        await _db.SaveChangesAsync(ct);
        return row.Id;
    }
}

/// <summary>Moves the tenant default. The default is what NULL-tagged config rows dispatch through, so this
/// is a routing decision — single transaction, filtered-unique index arbitrates races.</summary>
public record SetDefaultConnectionPointCommand(Guid Id) : IRequest<bool>;

public class SetDefaultConnectionPointCommandHandler : IRequestHandler<SetDefaultConnectionPointCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public SetDefaultConnectionPointCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<bool> Handle(SetDefaultConnectionPointCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        var target = await _db.ConnectionPoints.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == request.Id && p.TenantId == tid && !p.IsDeleted, ct);
        if (target is null) return false;
        if (target.IsDefault) return true;
        if (!OutboundTransportCatalog.Available.Contains(target.SystemType))
            throw new ValidationException($"'{target.SystemType}' has no registered outbound transport — it cannot be the default connection.");

        var current = await _db.ConnectionPoints.IgnoreQueryFilters()
            .Where(p => p.TenantId == tid && p.IsDefault && !p.IsDeleted)
            .ToListAsync(ct);
        foreach (var p in current) { p.IsDefault = false; p.UpdatedBy = _user.UserCode; p.UpdatedOn = DateTime.UtcNow; }
        target.IsDefault = true;
        target.UpdatedBy = _user.UserCode;
        target.UpdatedOn = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

public record DeleteConnectionPointCommand(Guid Id) : IRequest<bool>;

public class DeleteConnectionPointCommandHandler : IRequestHandler<DeleteConnectionPointCommand, bool>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    public DeleteConnectionPointCommandHandler(IAppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<bool> Handle(DeleteConnectionPointCommand request, CancellationToken ct)
    {
        if (_user.TenantId is not { } tid) return false;
        var row = await _db.ConnectionPoints.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == request.Id && p.TenantId == tid && !p.IsDeleted, ct);
        if (row is null) return false;
        if (row.IsDefault)
            throw new ValidationException("The default connection cannot be deleted — set another default first.");
        var inUse = await _db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .CountAsync(c => c.ConnectionPointId == row.Id && !c.IsDeleted, ct);
        if (inUse > 0)
            throw new ValidationException($"{inUse} integration config(s) are tagged to this connection — retag them first.");

        row.IsDeleted = true;
        row.DeletedOn = DateTime.UtcNow;
        row.DeletedBy = _user.UserCode;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
