using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §3.4 / D7. Per-(portal-entity, attachment-type) IDM payload + eligibility config
/// (Settings › IDM Entity Type). Keyed unique on (tenant, <see cref="OwnerEntityType"/>, <see cref="AttachmentType"/>)
/// — a row is selected for a <c>DocumentUpload</c> whose <c>OwnerEntityType</c> matches and whose
/// <c>DocumentType</c> matches (or, when <see cref="AttachmentType"/> is NULL, ALL that entity's documents).
/// Admin-scoped integration config: tenant-scoped, not seccoded.
///
/// <para>2026-07-06 — <see cref="OwnerEntityType"/> is now STORED (was derived from the snapshot provider by
/// <see cref="IdmEntityType"/>) so <see cref="IdmEntityType"/> can be free text (e.g. a portal entity with no
/// snapshot provider yet — the row is then dormant until a provider is registered). <see cref="AttachmentType"/>
/// is now OPTIONAL: null = "every document of this portal entity" (pushed once its gate passes).</para>
/// </summary>
public class IdmAttachmentTypeConfig : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    /// <summary>Portal entity whose documents this row classifies: <c>Asn</c> | <c>Invoice</c> | <c>Supplier</c>
    /// (a <c>doc.DocumentUpload.OwnerEntityType</c> value). Nullable only for pre-2026-07-06 rows (resolved at
    /// read/worker time from the snapshot provider as a fallback).</summary>
    public string? OwnerEntityType { get; set; }

    /// <summary>Maps to <c>doc.AttachmentType.code</c> (nvarchar(50)); selected via <c>DocumentUpload.DocumentType</c>.
    /// NULL = catch-all: every document of <see cref="OwnerEntityType"/> matches (no per-type filter).</summary>
    public string? AttachmentType { get; set; }

    /// <summary>IDM entity type, e.g. <c>"InforInvoice"</c> — the config selector value and the <c>MDS_EntityType</c> payload value.</summary>
    public string IdmEntityType { get; set; } = string.Empty;

    /// <summary>JSON array of required-non-null snapshot dot-paths (the eligibility gate), evaluated against the owning-entity snapshot.</summary>
    public string EligibilityGateJson { get; set; } = string.Empty;

    /// <summary>JSONata mapping expression producing the Create request envelope <c>{ headers, body }</c>.</summary>
    public string CreateMappingExpression { get; set; } = string.Empty;

    /// <summary>JSONata mapping expression for pid-keyed Update/Delete. Null falls back to the create shape carrying pid.</summary>
    public string? MutateMappingExpression { get; set; }

    /// <summary>SHA-256 of the repo Create expression at last seed (D6). Drift = current text hash ≠ repo default hash; re-seed overwrites only when unchanged since last seed.</summary>
    public string? CreateMappingSeedHash { get; set; }

    /// <summary>SHA-256 of the repo Mutate expression at last seed (D6).</summary>
    public string? MutateMappingSeedHash { get; set; }

    /// <summary>Soft on/off switch (disabled by default; the seeding scan only picks up enabled types).</summary>
    public bool IsEnabled { get; set; }
}
