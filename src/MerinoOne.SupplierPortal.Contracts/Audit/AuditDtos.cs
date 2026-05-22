namespace MerinoOne.SupplierPortal.Contracts.Audit;

/// <summary>
/// One field-level audit row from <c>audit.AuditEntry</c> (TSD §7.6).
/// </summary>
public record AuditEntryDto(
    Guid Id,
    int Seq,
    string EntityName,
    Guid EntityId,
    string Operation,
    string FieldName,
    string? OldValue,
    string? NewValue,
    string ChangedBy,
    DateTime ChangedOn);
