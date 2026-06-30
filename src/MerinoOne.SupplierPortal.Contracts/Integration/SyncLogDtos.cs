namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R5 (TSD R5 Addendum §12 / Component 8) — inbound Sync Log read DTOs (proc.SyncLog). The admin "Sync Log" view
// lists every inbound call attempt with its status + error message inline; the raw payload is fetched on a
// by-id drill-in (the list never ships the full payload — Failed rows can carry a large nvarchar(max) payload).

/// <summary>One inbound Sync Log row for the admin list (the payload is fetched separately on drill-in).</summary>
public record SyncLogDto(
    Guid Id,
    int Seq,
    string Direction,
    string Api,
    string? EntityType,
    string? ExternalRef,
    string Status,
    string? ErrorMessage,
    bool HasPayload,
    DateTime ReceivedOn);
