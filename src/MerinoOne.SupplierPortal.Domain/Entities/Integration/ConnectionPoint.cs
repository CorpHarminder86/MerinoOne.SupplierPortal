using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R10 — a named outbound connection target (transport + auth boundary). Today every tenant has exactly one
/// (the seeded "Default — Infor ION" row, <see cref="IsDefault"/> = true, whose base URL and OAuth resolve
/// from the existing <see cref="InforConnectionSetting"/>). Adding a second system (Tally, another LN
/// instance, ClearTax) = one more row here + an <c>IOutboundTransport</c> implementation for its
/// <see cref="SystemType"/> — the config plane (<see cref="OutboundIntegrationConfig"/>) tags rows via
/// <c>connectionPointId</c> and never changes.
///
/// <para><see cref="SystemType"/> is CHECK-constrained to <see cref="ConnectionSystemTypes"/>. For
/// <c>InforION</c> rows <see cref="BaseUrl"/>/<see cref="AuthConfigJson"/> stay NULL (resolved from the
/// tenant's Infor connection settings — single source of truth for ION credentials). For other types the
/// transport reads them from here; <see cref="AuthConfigJson"/> is stored encrypted by the save handler.</para>
///
/// <para>Tenant-scoped admin config, NOT seccode-protected. Exactly one non-deleted default per tenant
/// (filtered unique index).</para>
/// </summary>
public class ConnectionPoint : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    /// <summary>Display name, unique per tenant, e.g. <c>"Default — Infor ION"</c>, <c>"Tally — Mumbai office"</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Transport selector — a <see cref="ConnectionSystemTypes"/> constant, CHECK-constrained.</summary>
    public string SystemType { get; set; } = ConnectionSystemTypes.InforIon;

    /// <summary>Base URL endpoint paths are appended to. NULL for InforION (resolved from the Infor connection).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Encrypted auth blob for non-ION transports (shape owned by the transport). NULL for InforION.</summary>
    public string? AuthConfigJson { get; set; }

    /// <summary>Exactly one per tenant — the connection used when a config row has no explicit tag.</summary>
    public bool IsDefault { get; set; }

    public string? Notes { get; set; }
}

/// <summary>Known transport types. A <c>ConnectionPoint.SystemType</c> is only assignable to a config row
/// when an <c>IOutboundTransport</c> is registered for it (save-time validation).</summary>
public static class ConnectionSystemTypes
{
    public const string InforIon = "InforION";
    public const string Tally = "Tally";
    public const string ClearTax = "ClearTax";
    public const string GenericRest = "GenericRest";

    public static readonly string[] All = { InforIon, Tally, ClearTax, GenericRest };
}
