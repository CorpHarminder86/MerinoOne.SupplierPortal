using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §3.4 / D7. Per-attachment-type IDM payload + eligibility config (Settings › Infor
/// IDM). Keyed unique on (tenant, <see cref="AttachmentType"/>) — the row is selected via
/// <c>DocumentUpload.DocumentType</c>. Many attachment types MAY map to one <see cref="IdmEntityType"/>
/// (so the UQ is NOT on idmEntityType). Admin-scoped integration config: tenant-scoped, not seccoded.
/// </summary>
public class IdmAttachmentTypeConfig : AuditableEntity, ITenantOwned
{
    public Guid? TenantId { get; set; }

    /// <summary>Maps to <c>doc.AttachmentType.code</c> (nvarchar(50)); selected via <c>DocumentUpload.DocumentType</c>.</summary>
    public string AttachmentType { get; set; } = string.Empty;

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
