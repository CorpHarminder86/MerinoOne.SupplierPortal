using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R9 (TSD R9 §2.5, D-R9-10/19) — one backfill run per gate-change propagation: the MANDATORY dry-run
/// snapshot (enqueue / re-arm / withdraw sets with row lists, frozen as JSON) and — when the admin
/// deliberately applies — the apply record. Apply is refused when <see cref="GateVersion"/> no longer
/// matches the config (the gate moved since the preview — re-run). Never deleted; this is the audit
/// trail for the highest-blast-radius admin action in the integration surface.
/// </summary>
public class LnBackfillRun : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }
    public Guid OutboundIntegrationConfigId { get; set; }
    public OutboundIntegrationConfig? OutboundIntegrationConfig { get; set; }

    /// <summary>Denormalised for the monitoring list (the config row may be soft-deleted later).</summary>
    public string TransactionType { get; set; } = string.Empty;

    /// <summary>The config's gateVersion the dry-run evaluated — apply demands it still matches.</summary>
    public int GateVersion { get; set; }

    /// <summary>CHECK-constrained: <c>DryRun</c> (preview computed) | <c>Applied</c> | <c>Superseded</c> (a newer dry-run exists) | <c>Discarded</c>.</summary>
    public string Status { get; set; } = "DryRun";

    public int EnqueueCount { get; set; }
    public int RearmCount { get; set; }
    public int WithdrawCount { get; set; }

    /// <summary>The full preview (row lists per set) as JSON — what the admin saw before applying.</summary>
    public string DryRunResultJson { get; set; } = string.Empty;

    public DateTime? AppliedOn { get; set; }
    public string? AppliedBy { get; set; }

    /// <summary>Per-row apply outcomes (incl. RacedAway / EscapedToSending / AlreadyLive) as JSON.</summary>
    public string? ApplyResultJson { get; set; }
}
