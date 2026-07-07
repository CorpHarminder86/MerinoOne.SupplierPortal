using MerinoOne.SupplierPortal.Domain.Entities.Integration;

namespace MerinoOne.SupplierPortal.Application.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.4 sweep / §2.5 backfill — O-R9-9: ONE scanner for both) — scans a config's entity set:
/// the code-registered candidate filter narrows in SQL (mandatory superset pre-filter — a pure expression
/// sweep degrades badly at volume), the deterministic key is derived with the SAME material as the
/// enqueue site (golden-pinned by unit test), the live outbox status per key is attached, and the JSONata
/// gate is evaluated per candidate in memory.
/// </summary>
public interface ILnGateScanner
{
    Task<IReadOnlyList<LnScanVerdict>> ScanAsync(OutboundIntegrationConfig config, int maxCandidates, CancellationToken ct = default);
}

/// <summary>
/// One scanned candidate: its derived deterministic key, the live outbox row's status on that key (null =
/// never enqueued), and the gate verdict. EntityName rides along because two portalEntities enqueue under
/// a DIFFERENT outbox entityName than their own (PoNegotiation rows carry EntityName=PurchaseOrder).
/// </summary>
public sealed record LnScanVerdict(
    Guid EntityId,
    Guid TenantId,
    string EntityName,
    string DeterministicKey,
    string? ExistingRowStatus,
    Guid? ExistingRowId,
    bool Eligible,
    string? Reason,
    int? GateVersion);
