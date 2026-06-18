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
    DateTime CreatedOn);

// Create requests
public record CreateDeliveryTermRequest(string Code, string Description, bool IsActive = true);
public record CreatePaymentTermRequest(string Code, string Description, int NetDays, bool IsActive = true);
public record CreateItemRequest(string Code, string Description, string? HsnCode, Guid? ItemGroupId = null, Guid? UnitId = null, bool IsActive = true);

// Update requests (Code remains immutable to keep it stable for FK lookups; rest editable)
public record UpdateDeliveryTermRequest(string Description, bool IsActive);
public record UpdatePaymentTermRequest(string Description, int NetDays, bool IsActive);
public record UpdateItemRequest(string Description, string? HsnCode, Guid? ItemGroupId, Guid? UnitId, bool IsActive);

/// <summary>
/// Convenience upsert payload used by the admin Items page — accepts code on every save
/// so the page can use one form for both create + edit. The server still rejects code
/// changes on existing rows (Code is immutable post-creation).
/// </summary>
public record UpsertItemRequest(string Code, string Description, string? HsnCode, Guid? ItemGroupId = null, Guid? UnitId = null, bool IsActive = true);
