using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.DeliverySchedules;

/// <summary>
/// R5 (TSD R5 Addendum §8.1 / §8.2 / §4.4), R13 (2026-08-05) — the SINGLE shared point that materialises a PO's
/// delivery schedule set. Given a PO (with its lines, each carrying its own warehouse), it UPSERTS one
/// <see cref="DeliverySchedule"/> per non-deleted PO line keyed on <c>PurchaseOrderLineId</c>:
/// <list type="bullet">
///   <item><b>Create</b> when no active schedule exists for the line.</item>
///   <item><b>Refresh in place</b> (date / qty / warehouse) when one already exists — never duplicated. The
///         filtered unique index <c>UQ_DeliverySchedule_line</c> is the DB backstop.</item>
/// </list>
///
/// <para>This is the dedup point for BOTH triggers (§8.1 PO-becomes-shippable, §8.2 material Modify
/// re-confirmation). Because it is idempotent, the multiple call sites (Accept / Acknowledge / AutoAccept ingest /
/// AutoRelease / material-modify re-confirm) all funnel through here without duplicating creation logic.</para>
///
/// <para><b>Phase 1 line-removal handling (§8.2):</b> a removed line's schedule is left in place (soft-handled),
/// NOT hard-deleted — schedules for lines absent from the current PO are simply not touched (they may carry
/// shipped history). Only schedules for lines PRESENT on the PO are upserted. Multi-schedule splits + active
/// soft-delete of an orphaned schedule are deferred to Phase 2.</para>
///
/// <para><b>Scope:</b> schedules are BaseAggregateRoot. The scope columns are copied DETERMINISTICALLY from the
/// owning PO (SeccodeId / TenantId / TenantEntityId) rather than relying on the request-scope stamp interceptor —
/// the helper runs under BOTH a supplier principal (Accept/Acknowledge) and the inbound system principal
/// (AutoAccept ingest), and the SeccodeId is never auto-stamped, so it must be set explicitly here.</para>
///
/// <para><b>Warehouse gate (§8.1, R13):</b> a PO LINE with no resolved <c>WarehouseAddressId</c> (a legacy/pre-R13
/// line) is skipped gracefully — no schedule is created for it (the warehouse is the schedule's ASN grouping key,
/// and there is nothing valid to point at). Warehouse is per line now — the PO header no longer carries ship-to.</para>
///
/// <para>The caller is responsible for the surrounding <c>SaveChangesAsync</c> — the helper only stages the
/// Add / in-place mutation onto the tracked context so it commits in the same transaction as the PO transition.</para>
/// </summary>
public sealed class DeliveryScheduleFactory
{
    private readonly IAppDbContext _db;

    public DeliveryScheduleFactory(IAppDbContext db) => _db = db;

    /// <summary>
    /// Query-by-id entry point — used by the supplier confirmation commands (Accept / Acknowledge) and the
    /// AutoRelease command, where the PO + its lines are already persisted. Loads the PO scope + its non-deleted
    /// lines (each with its warehouse), then upserts. No-op (returns 0) when the PO is gone or has no lines.
    ///
    /// <para><paramref name="refreshOnly"/> = <c>true</c> (the §8.2 material-Modify path) REFRESHES existing
    /// schedules in place but does NOT create new ones — a material Modify resets an AcceptToShip /
    /// AcknowledgeToShip PO to Released (un-confirmed), so the helper must not fabricate a schedule set the
    /// supplier has not (re-)confirmed; the re-confirm command re-creates the rest via the default path.</para>
    /// </summary>
    public async Task<int> EnsureDeliverySchedulesAsync(Guid purchaseOrderId, CancellationToken ct, bool refreshOnly = false)
    {
        // Service-principal-safe read: the inbound path has no seccode/company context (IgnoreQueryFilters), and
        // the supplier path already sees its own PO under RLS — IgnoreQueryFilters is harmless there (we re-scope
        // by id). One projection: PO scope + its non-deleted lines (id/qty/date/warehouse — R13: per-line warehouse).
        var po = await _db.PurchaseOrders.IgnoreQueryFilters()
            .Where(p => p.Id == purchaseOrderId && !p.IsDeleted)
            .Select(p => new PoScheduleContext(
                p.Id, p.SeccodeId, p.TenantId, p.TenantEntityId,
                p.Lines.Where(l => !l.IsDeleted)
                    .Select(l => new PoScheduleLine(l.Id, l.OrderQty, l.DeliveryDate, l.WarehouseAddressId))
                    .ToList()))
            .FirstOrDefaultAsync(ct);

        if (po is null) return 0;
        return await UpsertCoreAsync(po, refreshOnly, ct);
    }

    /// <summary>
    /// Entity entry point — used by the INBOUND AutoAccept ingest auto-stamp, where a BRAND-NEW PO + its lines are
    /// tracked-but-NOT-yet-flushed (a query would not see them). Reads the scope + lines (each with its warehouse)
    /// straight off the in-memory aggregate. Same create-or-refresh semantics as the by-id path.
    /// </summary>
    public async Task<int> EnsureDeliverySchedulesAsync(PurchaseOrder po, CancellationToken ct, bool refreshOnly = false)
    {
        var lines = po.Lines.Where(l => !l.IsDeleted)
            .Select(l => new PoScheduleLine(l.Id, l.OrderQty, l.DeliveryDate, l.WarehouseAddressId))
            .ToList();
        var ctx = new PoScheduleContext(po.Id, po.SeccodeId, po.TenantId, po.TenantEntityId, lines);
        return await UpsertCoreAsync(ctx, refreshOnly, ct);
    }

    private async Task<int> UpsertCoreAsync(PoScheduleContext po, bool refreshOnly, CancellationToken ct)
    {
        // A PO with no lines → skip gracefully. (R13: the per-PO ship-to gate is gone — warehouse is per line now,
        // so the skip is applied per line below rather than for the whole PO.)
        if (po.Lines.Count == 0) return 0;

        var lineIds = po.Lines.Select(l => l.Id).ToList();

        // Existing active schedules for these lines (the upsert match set). One round-trip; tracked so the refresh
        // is an in-place UPDATE. IgnoreQueryFilters for the same service-principal reason as the PO read.
        var existing = await _db.DeliverySchedules.IgnoreQueryFilters()
            .Where(s => !s.IsDeleted && lineIds.Contains(s.PurchaseOrderLineId))
            .ToListAsync(ct);
        var byLine = existing
            .GroupBy(s => s.PurchaseOrderLineId)
            .ToDictionary(g => g.Key, g => g.First());

        var now = DateTime.UtcNow;
        var processed = 0;

        foreach (var line in po.Lines)
        {
            // R13 (§8.1) — the warehouse (copied from the PO LINE) is the schedule's ASN grouping key. A line with
            // no resolved warehouse (legacy/pre-R13) is skipped gracefully — nothing valid to point at, and a
            // null-warehouse schedule could never be selected into a warehouse-grouped ASN. Mirrors the old per-PO
            // ship-to skip, now applied per line.
            if (line.WarehouseAddressId is not Guid warehouseAddressId || warehouseAddressId == Guid.Empty)
                continue;

            if (byLine.TryGetValue(line.Id, out var sched))
            {
                // Refresh in place (§8.2) — date / qty / warehouse. Status stays Approved (Phase 1). No duplicate row.
                sched.ScheduledQty = line.OrderQty;
                sched.DeliveryDate = line.DeliveryDate ?? now;
                sched.WarehouseAddressId = warehouseAddressId;
                sched.Status = DeliveryScheduleStatus.Approved;
                sched.UpdatedBy = "system:delivery-schedule";
                sched.UpdatedOn = now;
            }
            else if (refreshOnly)
            {
                // §8.2 refresh-only — no existing schedule for this line, and we must not create one (the PO is
                // un-confirmed mid-Modify). Skip; the re-confirm command creates it via the default path.
                continue;
            }
            else
            {
                // Create (§8.1) — one schedule per line. Scope copied from the owning PO (deterministic; the
                // SeccodeId is never auto-stamped, and the inbound principal sets scope explicitly).
                _db.DeliverySchedules.Add(new DeliverySchedule
                {
                    Id = Guid.NewGuid(),
                    PurchaseOrderId = po.Id,
                    PurchaseOrderLineId = line.Id,
                    WarehouseAddressId = warehouseAddressId,
                    ScheduledQty = line.OrderQty,
                    DeliveryDate = line.DeliveryDate ?? now,
                    Status = DeliveryScheduleStatus.Approved,
                    SeccodeId = po.SeccodeId,
                    TenantId = po.TenantId,
                    TenantEntityId = po.TenantEntityId,
                    CreatedBy = "system:delivery-schedule",
                    CreatedOn = now,
                });
            }
            processed++;
        }

        return processed;
    }

    private sealed record PoScheduleContext(
        Guid Id, Guid SeccodeId, Guid? TenantId, Guid? TenantEntityId,
        IReadOnlyList<PoScheduleLine> Lines);

    // R13 — warehouse moved onto the line (the PO header no longer carries ship-to/warehouse).
    private sealed record PoScheduleLine(Guid Id, decimal OrderQty, DateTime? DeliveryDate, Guid? WarehouseAddressId);
}
