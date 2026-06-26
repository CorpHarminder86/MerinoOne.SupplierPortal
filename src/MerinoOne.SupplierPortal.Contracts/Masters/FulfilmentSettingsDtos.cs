namespace MerinoOne.SupplierPortal.Contracts.Masters;

// ====================================================================================================
// R4 (2026-06-26) — Phase 5a (TSD R4 Addendum §7.4 + §8.5 + D5). DTOs for the admin Settings CRUD surface:
//   - Supplier-item over-ship tolerance override grid (Component 4).
//   - Attachment-type catalogue, attachment-entity reference list, and two-tier attachment-policy grid
//     (Component 5).
// All admin-gated (Settings.Read / Settings.Write).
// ====================================================================================================

// ---- Supplier-item over-ship tolerance (§7.4) ------------------------------------------------------

/// <summary>
/// One row in the supplier's item-tolerance grid: the item master + its master tolerance, the supplier's
/// override (nullable — null = inherit), and the RESOLVED tolerance (override ?? master) the ASN guard uses.
/// </summary>
public record SupplierItemToleranceDto(
    Guid ItemId,
    string ItemCode,
    string ItemDescription,
    decimal ItemMasterTolerancePct,
    decimal? SupplierOverridePct,
    decimal ResolvedTolerancePct,
    // The SupplierItem row id when an override exists (for DELETE-by-id); null when the supplier inherits.
    Guid? SupplierItemId);

/// <summary>
/// Upsert a supplier's item tolerance override. <paramref name="OverShipTolerancePct"/> null = inherit
/// (clears the override — stored as NULL per the nullable semantics); a value = explicit cap (0 = no over-ship).
/// </summary>
public record UpsertSupplierItemToleranceRequest(
    Guid SupplierId,
    Guid ItemId,
    decimal? OverShipTolerancePct);

// ---- Attachment-type catalogue (§8.5) --------------------------------------------------------------

public record AttachmentTypeDto(
    Guid Id,
    int Seq,
    string Code,
    string Name,
    bool IsActive,
    DateTime CreatedOn);

public record CreateAttachmentTypeRequest(string Code, string Name);
// Code is immutable post-creation (it aligns with DocumentUpload.documentType); only Name / IsActive editable.
public record UpdateAttachmentTypeRequest(string Name, bool IsActive);

// ---- Attachment-entity reference (read-only, §8.5) -------------------------------------------------

public record AttachmentEntityDto(
    Guid Id,
    int Seq,
    string Code,
    string Name,
    bool IsActive,
    DateTime CreatedOn);

// ---- Attachment requirement policy (§8.5 + D5) -----------------------------------------------------

/// <summary>
/// One policy row in the requirements grid for an entity: the type, whether this is a tenant default
/// (SupplierId null) or a supplier override, and the requirement level. <paramref name="EffectiveRequirement"/>
/// is the D5 supplier-wins resolution (supplier override ?? tenant default ?? Optional) surfaced for the grid.
/// </summary>
public record AttachmentPolicyDto(
    Guid Id,
    string AttachmentEntityCode,
    Guid AttachmentTypeId,
    string AttachmentTypeCode,
    string AttachmentTypeName,
    Guid? SupplierId,
    string Requirement,
    string EffectiveRequirement,
    bool IsActive);

/// <summary>
/// Upsert a policy row. Identify the entity + type by code (the admin grid works in codes). SupplierId null =
/// tenant default; non-null = supplier override (D5). Requirement ∈ {Mandatory, Warning, Optional}; upsert is
/// keyed on the appropriate D5 unique (tenant-default vs supplier-override).
/// </summary>
public record UpsertAttachmentPolicyRequest(
    string AttachmentEntityCode,
    string AttachmentTypeCode,
    Guid? SupplierId,
    string Requirement);
