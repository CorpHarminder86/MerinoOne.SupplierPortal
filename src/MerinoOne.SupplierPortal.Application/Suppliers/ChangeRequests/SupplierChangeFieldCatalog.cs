using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests;

/// <summary>
/// The per-target allow-list of editable field names for a change-request <c>Edit</c> line, plus the typed
/// validation each field reuses from module 1 (e.g. the bank IFSC regex, license expiry≥issue). This is the
/// single source of truth shared by:
/// <list type="bullet">
///   <item>the create/update validator (rejects an Edit naming a field outside the allow-list, or a malformed value);</item>
///   <item>the typed appliers (which patch ONLY allow-listed named fields onto the live row on approve).</item>
/// </list>
/// Keeping the catalogue explicit (not reflection) means an unknown/disallowed field is a 400 at create time and
/// can never reach the live row — the appliers stay deterministic and typed. APPEND-ONLY when new editable fields
/// are exposed to suppliers.
/// </summary>
public static class SupplierChangeFieldCatalog
{
    /// <summary>Allowed Edit field names per target. Scalar Supplier-level fields the supplier may propose to change.</summary>
    public static readonly IReadOnlyDictionary<ChangeTargetEntity, IReadOnlySet<string>> EditableFields =
        new Dictionary<ChangeTargetEntity, IReadOnlySet<string>>
        {
            [ChangeTargetEntity.Supplier] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "LegalName", "TradeName", "Website", "GstNumber", "PanNumber", "MsmeRegNumber", "MsmeCategory",
            },
            [ChangeTargetEntity.Address] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AddressType", "AddressLine1", "AddressLine2", "Area", "City", "State", "Pincode", "Country",
            },
            [ChangeTargetEntity.Contact] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ContactName", "Designation", "Email", "Phone", "IsPrimary", "AddressId",
            },
            [ChangeTargetEntity.Bank] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BankName", "BankAddress", "AccountName", "AccountNumber", "IfscCode", "SwiftCode", "IsPrimary",
            },
            [ChangeTargetEntity.License] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "LicenseNumber", "LicenseType", "Remarks", "IssueDate", "ExpiryDate",
            },
        };

    public static bool IsEditableField(ChangeTargetEntity target, string? fieldName)
        => !string.IsNullOrWhiteSpace(fieldName)
           && EditableFields.TryGetValue(target, out var fields)
           && fields.Contains(fieldName.Trim());
}
