using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R5 (TSD R5 Addendum §4.9 / Component 8) — inbound integration sync log.
/// Records every inbound API call; on failure captures the error message for the admin sync-log
/// view (§12). Tenant-scoped (tenantId from the inherited ITenantScoped via BaseAggregateRoot).
/// Rows are insert-only in practice (no updates after creation); they carry the standard audit
/// + soft-delete envelope from AuditableEntity so the global soft-delete filter applies.
/// </summary>
public class SyncLog : BaseAggregateRoot
{
    /// <summary>
    /// Direction of the sync call. Defaults to 'Inbound' (the only direction currently used
    /// for this log entity); mapped as <c>nvarchar(20)</c> with DB default 'Inbound'.
    /// </summary>
    public string Direction { get; set; } = "Inbound";

    /// <summary>Inbound API label, e.g. 'PO Inbound', 'GRN Status Inbound'.</summary>
    public string Api { get; set; } = string.Empty;

    /// <summary>The entity type being synced, e.g. 'PurchaseOrder'. Nullable.</summary>
    public string? EntityType { get; set; }

    /// <summary>External reference identifier, e.g. the PO number. Nullable.</summary>
    public string? ExternalRef { get; set; }

    /// <summary>Outcome of the call: 'Success' or 'Failed'.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Human-readable error captured on failure, e.g. "ERP status 'OnHold' is not mapped". Nullable; nvarchar(max).</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Raw inbound payload for diagnostic replay. Nullable; nvarchar(max).</summary>
    public string? Payload { get; set; }

    /// <summary>UTC timestamp when the inbound call was received.</summary>
    public DateTime ReceivedOn { get; set; }
}
