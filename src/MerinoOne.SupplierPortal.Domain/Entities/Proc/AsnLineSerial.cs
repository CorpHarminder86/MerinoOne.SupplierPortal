using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Proc;

/// <summary>
/// R4 (2026-06-23) — Serial capture for a serialized ASN line. CHILD of the ASN aggregate, reached only via
/// <see cref="AsnLine"/> (itself a child of <see cref="Asn"/>): it carries no seccode/tenant of its own — the
/// owning <see cref="Asn"/> root carries the seccode RLS, so these rows inherit visibility through the aggregate.
/// Modeled as <see cref="AuditableEntity"/> (two-key + audit block only), mirroring <see cref="AsnLine"/> /
/// <see cref="AsnPurchaseOrder"/>. Serialized and lot-controlled are mutually exclusive per item (Item XOR guard).
/// </summary>
public class AsnLineSerial : AuditableEntity
{
    public Guid AsnLineId { get; set; }
    public AsnLine? AsnLine { get; set; }

    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// R4 (2026-06-30) — per-serial expiry (one physical unit = one serial = one expiry). Nullable + lenient,
    /// mirroring <see cref="AsnLineLot.ExpiryDate"/> (no required / future-date rule — parity with lot capture).
    /// </summary>
    public DateOnly? ExpiryDate { get; set; }

    public string? ErpCode { get; set; }
}
