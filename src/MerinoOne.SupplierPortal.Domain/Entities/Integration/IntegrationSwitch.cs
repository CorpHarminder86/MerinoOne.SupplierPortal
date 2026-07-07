using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R9 (TSD R9 §2.6, D-R9-11) — DB-backed kill switch, one row per (tenant, scope), read by the outbox
/// dispatcher once per drain cycle. Scopes: <c>OutboundGlobal</c> (dispatcher drains nothing for the
/// tenant; enqueue continues — killing enqueue silently loses business events) and <c>InboundErpAck</c>
/// (/inbound/erp-ack accepts-and-holds: raw acks persist to <see cref="HeldInboundMessage"/> and replay
/// on re-enable — never a 503; LN's retry behaviour is not ours to trust). The per-endpoint scope is
/// NOT here — that is <c>OutboundIntegrationConfig.DispatchMode = Held</c>.
/// ABSENT ROW = ENABLED (rows are lazy-created on first toggle; no per-tenant seeding).
/// Every toggle writes an <see cref="IntegrationSwitchAudit"/> row with a MANDATORY reason.
/// </summary>
public class IntegrationSwitch : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    /// <summary>Scope discriminator — CHECK-constrained: <c>OutboundGlobal</c> | <c>InboundErpAck</c>.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>True = flowing (the default state an absent row represents); false = killed.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The reason given on the most recent toggle (full history in the audit table).</summary>
    public string? LastReason { get; set; }
}

/// <summary>Scope constants for <see cref="IntegrationSwitch.Scope"/>. APPEND-ONLY.</summary>
public static class IntegrationSwitchScope
{
    public const string OutboundGlobal = "OutboundGlobal";
    public const string InboundErpAck = "InboundErpAck";
}

/// <summary>
/// R9 (§2.6) — immutable audit row per kill-switch toggle: who (audit CreatedBy), when (CreatedOn),
/// old → new state, and the MANDATORY reason note.
/// </summary>
public class IntegrationSwitchAudit : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }
    public Guid IntegrationSwitchId { get; set; }
    public IntegrationSwitch? IntegrationSwitch { get; set; }
    public string Scope { get; set; } = string.Empty;
    public bool OldEnabled { get; set; }
    public bool NewEnabled { get; set; }
    public string Reason { get; set; } = string.Empty;
}
