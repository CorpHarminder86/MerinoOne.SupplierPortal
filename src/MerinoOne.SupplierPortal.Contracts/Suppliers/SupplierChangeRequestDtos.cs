namespace MerinoOne.SupplierPortal.Contracts.Suppliers;

// ============================================================================================
// R4 Module 2 — Supplier Change Management contracts.
//
// The supplier raises a change request (never mutating live data); an internal reviewer approves
// (applying the deltas + pushing per line to ERP) or rejects / bounces back. These DTOs are consumed
// by the blazor turn: list grid, detail + diff view (old→new per line), and the request/approve forms.
//
// All enum-valued fields are serialized as the enum NAME (string) — the same convention the entity uses
// (no DB CHECK; the C# enum is the guard) — so the UI binds against stable string values.
// ============================================================================================

/// <summary>One row in the change-request list grid. <see cref="LineCount"/> is the delta-line count.</summary>
public record SupplierChangeRequestListItemDto(
    Guid Id,
    int Seq,
    Guid SupplierId,
    string SupplierCode,
    string SupplierLegalName,
    string ChangeStatus,
    string? Summary,
    string RequestedBy,
    DateTime RequestedAt,
    string? ReviewedBy,
    DateTime? ReviewedAt,
    int LineCount,
    DateTime CreatedOn);

/// <summary>Full change-request detail: header + lines (each carrying the old→new diff + per-line push state).</summary>
public record SupplierChangeRequestDto(
    Guid Id,
    int Seq,
    Guid SupplierId,
    string SupplierCode,
    string SupplierLegalName,
    string ChangeStatus,
    string? Summary,
    string RequestedBy,
    DateTime RequestedAt,
    string? ReviewedBy,
    DateTime? ReviewedAt,
    string? RejectionReason,
    DateTime CreatedOn,
    List<SupplierChangeRequestLineDto> Lines);

/// <summary>
/// A single proposed delta. For <c>Edit</c> the diff view shows <see cref="FieldName"/>: <see cref="OldValue"/>
/// → <see cref="NewValue"/>. For <c>Add</c> the proposed row state is in <see cref="PayloadJson"/> (the diff view
/// renders the parsed object). For <c>Delete</c> <see cref="TargetEntityId"/> identifies the row being removed.
/// <see cref="PushStatus"/> / <see cref="PushedAt"/> / <see cref="ErpRef"/> are the per-line ERP push state
/// (post-approval; <see cref="ErpRef"/> is filled later by the /inbound/erp-ack endpoint).
/// </summary>
public record SupplierChangeRequestLineDto(
    Guid Id,
    string TargetEntity,     // Supplier | Address | Contact | Bank | License
    Guid? TargetEntityId,
    string Operation,        // Add | Edit | Delete
    string? FieldName,
    string? OldValue,
    string? NewValue,
    string? PayloadJson,
    string PushStatus,       // Pending | Pushed | PushFailed
    DateTime? PushedAt,
    string? ErpRef);

// ---------------- Request bodies ----------------

/// <summary>
/// A proposed delta line in a create/replace request. The handler validates per-target (reusing module-1 rules):
/// <list type="bullet">
///   <item><c>Add</c> — <see cref="PayloadJson"/> required (the new row's fields); <see cref="TargetEntityId"/> null.</item>
///   <item><c>Edit</c> — <see cref="TargetEntityId"/> + <see cref="FieldName"/> + <see cref="NewValue"/> required.</item>
///   <item><c>Delete</c> — <see cref="TargetEntityId"/> required.</item>
/// </list>
/// </summary>
public record SupplierChangeLineInput(
    string TargetEntity,     // Supplier | Address | Contact | Bank | License
    string Operation,        // Add | Edit | Delete
    Guid? TargetEntityId = null,
    string? FieldName = null,
    string? NewValue = null,
    string? PayloadJson = null);

/// <summary>Supplier raises a change request (status Draft). At least one line is required on submit.</summary>
public record CreateSupplierChangeRequestRequest(
    Guid SupplierId,
    string? Summary,
    List<SupplierChangeLineInput> Lines);

/// <summary>Replace the editable line-set of a Draft / ChangesRequested request (supplier-only).</summary>
public record UpdateSupplierChangeRequestRequest(
    string? Summary,
    List<SupplierChangeLineInput> Lines);

/// <summary>Reviewer bounces the request back to the supplier for amendment (→ ChangesRequested).</summary>
public record RequestChangesRequest(string Reason);

/// <summary>Reviewer rejects the request (→ Rejected). Reason is required.</summary>
public record RejectSupplierChangeRequest(string Reason);
