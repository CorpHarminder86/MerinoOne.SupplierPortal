namespace MerinoOne.SupplierPortal.Application.Common.Documents;

/// <summary>
/// Canonical <c>doc.DocumentUpload.OwnerEntityType</c> discriminator values used by the polymorphic
/// document-attachment paths. Centralised so the controller (upload/list/delete) and the license commands
/// (deferred-upload rebind) agree on the exact strings. NOT an enum — the column has no DB CHECK and is a
/// free-text discriminator shared across modules; appending a value here is additive and migration-free.
/// </summary>
public static class DocumentOwnerTypes
{
    /// <summary>A supplier license / certification (1:N attachments). ownerEntityId = SupplierLicense.Id.</summary>
    public const string SupplierLicense = "SupplierLicense";

    /// <summary>
    /// R4 (2026-06-22) — Module 3: an ASN's multi-attachments (1:N). ownerEntityId = Asn.Id, DocumentType =
    /// AsnAttachment. Attachable while the ASN is Draft; locked (attach/delete rejected) once Submitted.
    /// </summary>
    public const string Asn = "Asn";

    /// <summary>Self-registration onboarding documents bound to a pending invite. ownerEntityId = SupplierInvite.Id.</summary>
    public const string PendingInvite = "PendingInvite";

    /// <summary>An approved supplier's master documents. ownerEntityId = Supplier.Id.</summary>
    public const string Supplier = "Supplier";

    /// <summary>
    /// R4 (2026-06-26) — Phase 4: an invoice's attachments (e.g. tax invoice / e-invoice / e-way bill).
    /// ownerEntityId = Invoice.Id. Aligns with the seeded <c>doc.AttachmentEntity</c> code "Invoice" so the
    /// attachment-requirement policy evaluator matches uploads to the Invoice entity.
    /// </summary>
    public const string Invoice = "Invoice";

    /// <summary>
    /// Deferred-upload holding pen: files uploaded by a logged-in user BEFORE the owning aggregate
    /// (e.g. a new SupplierLicense) has been saved. ownerEntityId = a client-generated draft GUID
    /// (the "staging key"). The owning command re-points these rows to the real owner on save.
    /// </summary>
    public const string Staging = "Staging";
}
