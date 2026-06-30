using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Common.Integration;

/// <summary>
/// R5 (TSD R5 Addendum §12 / Component 8) — default <see cref="ISyncLogWriter"/>. Adds a <c>proc.SyncLog</c>
/// row, tenant-scoped, owned by the tenant's config seccode (the row is <c>BaseAggregateRoot</c> and carries a
/// required Seccode FK — resolved from an existing tenant config/proc row, mirroring the Settings masters).
///
/// <para><b>Payload only on FAILED rows</b> (SQL-Express 10 GB cap — see <see cref="ISyncLogWriter"/>):
/// <see cref="WriteSuccessAsync"/> stores no payload; <see cref="WriteFailedAsync"/> stores the raw payload for
/// diagnosis. <c>CreatedBy="infor:inbound"</c> so the audit interceptor short-circuits (no AuditEntry row per
/// log write — these are already an audit trail).</para>
/// </summary>
public class SyncLogWriter : ISyncLogWriter
{
    private const string CreatedBy = "infor:inbound";

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public SyncLogWriter(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public Task WriteSuccessAsync(string api, string? entityType, string? externalRef,
        Guid? tenantId = null, bool defer = false, CancellationToken ct = default)
        => AddAsync(api, entityType, externalRef, status: "Success", errorMessage: null, payload: null,
            tenantId, defer, ct);

    public Task WriteFailedAsync(string api, string? entityType, string? externalRef, string errorMessage,
        string? payload, Guid? tenantId = null, bool defer = false, CancellationToken ct = default)
        => AddAsync(api, entityType, externalRef, status: "Failed", errorMessage,
            // Payload is captured ONLY on failed rows (scalability — see ISyncLogWriter).
            payload, tenantId, defer, ct);

    private async Task AddAsync(string api, string? entityType, string? externalRef, string status,
        string? errorMessage, string? payload, Guid? tenantId, bool defer, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var tid = tenantId ?? _user.TenantId;
        var seccodeId = await ResolveSeccodeAsync(tid, ct);

        _db.SyncLogs.Add(new SyncLog
        {
            Id = Guid.NewGuid(),
            TenantId = tid,
            SeccodeId = seccodeId,
            Direction = "Inbound",
            Api = Cap(api, 80) ?? string.Empty,
            EntityType = Cap(entityType, 50),
            ExternalRef = Cap(externalRef, 100),
            Status = status,
            ErrorMessage = errorMessage,
            Payload = payload,
            ReceivedOn = now,
            CreatedBy = CreatedBy,
            CreatedOn = now,
        });

        // defer=true: only TRACK the row — the caller's open transaction commits it (the inbound executor adds the
        // SyncLog row to its own atomic flush so success/failure logging is consistent with the upsert).
        if (!defer) await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Resolves a valid Seccode for the log row's required FK: reuse an existing tenant config/proc row's seccode
    /// (a PoStatusMapping, then a Company, then an AttachmentType — all owned by the tenant-admin config seccode).
    /// Falls back to ANY seccode for the tenant (last resort) so a log write never fails on a missing FK; if the
    /// tenant is unknown, uses any seccode at all. Filter-bypassed reads (the writer runs under the service
    /// principal during inbound, which has no seccode context).
    /// </summary>
    private async Task<Guid> ResolveSeccodeAsync(Guid? tenantId, CancellationToken ct)
    {
        var fromMapping = await _db.PoStatusMappings.IgnoreQueryFilters()
            .Where(m => tenantId == null || m.TenantId == tenantId)
            .Select(m => (Guid?)m.SeccodeId).FirstOrDefaultAsync(ct);
        if (fromMapping is Guid s1 && s1 != Guid.Empty) return s1;

        var fromCompany = await _db.Companies.IgnoreQueryFilters()
            .Where(c => tenantId == null || c.TenantId == tenantId)
            .Select(c => (Guid?)c.SeccodeId).FirstOrDefaultAsync(ct);
        if (fromCompany is Guid s2 && s2 != Guid.Empty) return s2;

        var fromType = await _db.AttachmentTypes.IgnoreQueryFilters()
            .Where(t => tenantId == null || t.TenantId == tenantId)
            .Select(t => (Guid?)t.SeccodeId).FirstOrDefaultAsync(ct);
        if (fromType is Guid s3 && s3 != Guid.Empty) return s3;

        // Last resort — any seccode (so a SyncLog write never throws on the FK even on a barely-seeded tenant).
        var anySeccode = await _db.Seccodes.IgnoreQueryFilters()
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        return anySeccode ?? Guid.Empty;
    }

    private static string? Cap(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var v = value.Trim();
        return v.Length <= max ? v : v[..max];
    }
}
