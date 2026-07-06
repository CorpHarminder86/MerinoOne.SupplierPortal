namespace MerinoOne.SupplierPortal.Contracts.Integration;

// R9 (TSD R9 §2.6, D-R9-11) — kill-switch DTOs. Per-endpoint scope is NOT here (that is
// LnEndpointConfig.dispatchMode = Held on the config screen).

/// <summary>One switch scope's state. Absent DB row = enabled (rows lazy-create on first toggle).</summary>
public sealed record IntegrationSwitchDto(
    string Scope,
    bool IsEnabled,
    string? LastReason,
    string? LastChangedBy,
    DateTime? LastChangedOn,
    int HeldCount);

/// <summary>Toggle request — the reason is MANDATORY (every toggle is audited: who/when/why).</summary>
public sealed record ToggleIntegrationSwitchRequest(bool Enable, string Reason);

/// <summary>Audit history row.</summary>
public sealed record IntegrationSwitchAuditDto(
    string Scope,
    bool OldEnabled,
    bool NewEnabled,
    string Reason,
    string ChangedBy,
    DateTime ChangedOn);

/// <summary>Held inbound erp-ack row (accept-and-hold store).</summary>
public sealed record HeldInboundMessageDto(
    Guid Id,
    string EndpointName,
    string Status,
    int ReplayAttempts,
    string? LastError,
    DateTime ReceivedOn,
    DateTime? ReplayedOn);
