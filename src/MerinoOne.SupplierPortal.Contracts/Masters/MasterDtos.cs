namespace MerinoOne.SupplierPortal.Contracts.Masters;

/// <summary>
/// Uniform list/detail shape for DeliveryTerm + PaymentTerm + Item.
/// Type-specific fields live on the typed records below.
/// </summary>
public record MasterItemDto(
    Guid Id,
    int Seq,
    string Code,
    string Description,
    bool IsActive,
    DateTime CreatedOn);

public record DeliveryTermDto(
    Guid Id,
    int Seq,
    string Code,
    string Description,
    bool IsActive,
    DateTime CreatedOn);

public record PaymentTermDto(
    Guid Id,
    int Seq,
    string Code,
    string Description,
    int NetDays,
    bool IsActive,
    DateTime CreatedOn);

// ---- Tax (R4 Module 6) — company-shared (ICompanyScoped) master, cloned from DeliveryTerm. ----
public record TaxDto(
    Guid Id,
    int Seq,
    string Code,
    string Description,
    decimal? TaxRate,
    bool IsActive,
    DateTime CreatedOn);

public record CreateTaxRequest(string Code, string Description, decimal? TaxRate = null, bool IsActive = true);
public record UpdateTaxRequest(string Description, decimal? TaxRate, bool IsActive);

public record ItemDto(
    Guid Id,
    int Seq,
    string Code,
    string Description,
    string? HsnCode,
    Guid? ItemGroupId,
    string? ItemGroupCode,
    Guid? UnitId,
    string? UnitCode,
    bool IsActive,
    DateTime CreatedOn,
    // R4 A3: LN-fed control flags. ERP is the authority; surfaced read-only in the admin grid and they
    // drive ASN lot/serial capture. Trailing optional so existing positional constructions stay valid.
    bool IsSerialized = false,
    bool IsLotControlled = false,
    // R4 (2026-06-26) — §7.4: the item-master over-ship tolerance floor (% — NOT NULL, default 0). The
    // SupplierItem override (when present) wins; this is the fallback. Trailing optional so existing
    // positional constructions stay valid.
    decimal OverShipTolerancePct = 0m);

// Create requests
public record CreateDeliveryTermRequest(string Code, string Description, bool IsActive = true);
public record CreatePaymentTermRequest(string Code, string Description, int NetDays, bool IsActive = true);
// R4 (2026-06-26) — §7.4: OverShipTolerancePct optional (trailing); null → defaults to the entity default (0).
public record CreateItemRequest(string Code, string Description, string? HsnCode, Guid? ItemGroupId = null, Guid? UnitId = null, bool IsActive = true, decimal? OverShipTolerancePct = null);

// Update requests (Code remains immutable to keep it stable for FK lookups; rest editable)
public record UpdateDeliveryTermRequest(string Description, bool IsActive);
public record UpdatePaymentTermRequest(string Description, int NetDays, bool IsActive);
// R4 (2026-06-26) — §7.4: OverShipTolerancePct optional (trailing); null leaves the stored value unchanged.
public record UpdateItemRequest(string Description, string? HsnCode, Guid? ItemGroupId, Guid? UnitId, bool IsActive, decimal? OverShipTolerancePct = null);

/// <summary>
/// Convenience upsert payload used by the admin Items page — accepts code on every save
/// so the page can use one form for both create + edit. The server still rejects code
/// changes on existing rows (Code is immutable post-creation).
/// </summary>
public record UpsertItemRequest(string Code, string Description, string? HsnCode, Guid? ItemGroupId = null, Guid? UnitId = null, bool IsActive = true);
