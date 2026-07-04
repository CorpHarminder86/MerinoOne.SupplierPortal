using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;

namespace MerinoOne.SupplierPortal.Application.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — shared helper that appends IDM <c>Update</c> outbox rows. Consumed twice:
/// the erp-ack gate-field-change trigger (D4b — <see cref="EnqueueOwnerUpdatesAsync"/>) and the sync-log
/// manual Re-push action (D4a — <see cref="EnqueueDocumentUpdateAsync"/>). It NEVER calls SaveChanges — the
/// caller owns the unit of work (erp-ack runs inside the inbound transactional executor; the command inside
/// its own handler). All lookups use <c>IgnoreQueryFilters()</c> + an explicit tenant predicate because the
/// callers run without seccode/user context.
/// </summary>
public interface IIdmOutboxEnqueuer
{
    /// <summary>
    /// D4b: for every already-synced (pid-bearing, non-deleted) <c>DocumentUpload</c> of the owner, append one
    /// <c>Update</c> row (<c>Pending</c>), deduped against an existing non-terminal row for that document.
    /// Returns the number of rows added. First-time-eligible documents (no pid) are ignored — their initial
    /// Create is produced by the worker's seeding scan.
    /// </summary>
    Task<int> EnqueueOwnerUpdatesAsync(IAppDbContext db, Guid tenantId, string ownerEntityType,
        Guid ownerEntityId, string actor, CancellationToken ct);

    /// <summary>
    /// D4a: append one <c>Update</c> row for a specific synced document. Returns the new row, or null when the
    /// document is missing/soft-deleted, has no pid, or already has a non-terminal row in its partition.
    /// </summary>
    Task<IdmDocumentOutbox?> EnqueueDocumentUpdateAsync(IAppDbContext db, Guid tenantId,
        Guid documentUploadId, string actor, CancellationToken ct);
}
