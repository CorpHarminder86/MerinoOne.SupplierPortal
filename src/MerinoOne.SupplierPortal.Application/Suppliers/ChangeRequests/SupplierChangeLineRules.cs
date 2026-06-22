using System.Text.Json;
using System.Text.RegularExpressions;
using MerinoOne.SupplierPortal.Application.Suppliers.Commands;
using MerinoOne.SupplierPortal.Contracts.Suppliers;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests;

/// <summary>
/// Shape + value validation for a single <see cref="SupplierChangeLineInput"/>, reused by both the create and the
/// update (replace-lines) validators. Per-target field rules reuse module 1's regexes/conditionals (IFSC pattern,
/// GST/PAN format, license expiry≥issue) so a change-request can never propose a value the direct module-1 command
/// would reject. Returns a flat list of human-readable error strings (empty = valid) keyed by the caller into the
/// FluentValidation failure set.
/// </summary>
public static class SupplierChangeLineRules
{
    private static readonly Regex GstRegex = new("^[0-9A-Z]{15}$", RegexOptions.Compiled);
    private static readonly Regex PanRegex = new("^[A-Z]{5}[0-9]{4}[A-Z]{1}$", RegexOptions.Compiled);

    /// <summary>Validates one line. <paramref name="index"/> is the 0-based position for error context.</summary>
    public static IReadOnlyList<string> Validate(SupplierChangeLineInput line, int index)
    {
        var errs = new List<string>();
        var prefix = $"Lines[{index}]";

        if (!Enum.TryParse<ChangeTargetEntity>(line.TargetEntity, ignoreCase: true, out var target))
        {
            errs.Add($"{prefix}: TargetEntity must be one of Supplier, Address, Contact, Bank, License.");
            return errs; // can't validate further without a target
        }
        if (!Enum.TryParse<ChangeOperation>(line.Operation, ignoreCase: true, out var op))
        {
            errs.Add($"{prefix}: Operation must be one of Add, Edit, Delete.");
            return errs;
        }

        switch (op)
        {
            case ChangeOperation.Add:
                if (string.IsNullOrWhiteSpace(line.PayloadJson))
                    errs.Add($"{prefix}: Add requires a payloadJson describing the new {target} row.");
                else
                    ValidateAddPayload(target, line.PayloadJson, prefix, errs);
                break;

            case ChangeOperation.Edit:
                if (target == ChangeTargetEntity.Supplier)
                {
                    // The single supplier aggregate — no per-row target id needed.
                }
                else if (!line.TargetEntityId.HasValue || line.TargetEntityId.Value == Guid.Empty)
                {
                    errs.Add($"{prefix}: Edit of a {target} requires the targetEntityId of the existing row.");
                }
                if (string.IsNullOrWhiteSpace(line.FieldName))
                    errs.Add($"{prefix}: Edit requires a fieldName.");
                else if (!SupplierChangeFieldCatalog.IsEditableField(target, line.FieldName))
                    errs.Add($"{prefix}: '{line.FieldName}' is not an editable {target} field.");
                else
                    ValidateEditValue(target, line.FieldName!, line.NewValue, prefix, errs);
                break;

            case ChangeOperation.Delete:
                if (target == ChangeTargetEntity.Supplier)
                    errs.Add($"{prefix}: the Supplier record itself cannot be deleted via a change request.");
                else if (!line.TargetEntityId.HasValue || line.TargetEntityId.Value == Guid.Empty)
                    errs.Add($"{prefix}: Delete of a {target} requires the targetEntityId of the existing row.");
                break;
        }

        return errs;
    }

    private static void ValidateEditValue(ChangeTargetEntity target, string field, string? newValue, string prefix, List<string> errs)
    {
        var value = newValue?.Trim();

        // Reuse module-1 field rules per target/field.
        switch (target, field.ToLowerInvariant())
        {
            case (ChangeTargetEntity.Supplier, "legalname"):
                if (string.IsNullOrWhiteSpace(value)) errs.Add($"{prefix}: LegalName cannot be empty.");
                else if (value.Length > 300) errs.Add($"{prefix}: LegalName must be ≤ 300 characters.");
                break;
            case (ChangeTargetEntity.Supplier, "gstnumber"):
                if (!string.IsNullOrWhiteSpace(value) && !GstRegex.IsMatch(value.ToUpperInvariant()))
                    errs.Add($"{prefix}: GST number must be 15 alphanumeric characters.");
                break;
            case (ChangeTargetEntity.Supplier, "pannumber"):
                if (!string.IsNullOrWhiteSpace(value) && !PanRegex.IsMatch(value.ToUpperInvariant()))
                    errs.Add($"{prefix}: PAN must be 5 letters + 4 digits + 1 letter (e.g., ABCDE1234F).");
                break;

            case (ChangeTargetEntity.Bank, "ifsccode"):
                if (!string.IsNullOrWhiteSpace(value) && !SupplierBankValidationRules.IfscPattern.IsMatch(value.ToUpperInvariant()))
                    errs.Add($"{prefix}: IFSC must match ^[A-Z]{{4}}0[A-Z0-9]{{6}}$.");
                break;
            case (ChangeTargetEntity.Bank, "accountnumber"):
                if (string.IsNullOrWhiteSpace(value)) errs.Add($"{prefix}: AccountNumber cannot be empty.");
                else if (value.Length > 64) errs.Add($"{prefix}: AccountNumber must be ≤ 64 characters.");
                break;
            case (ChangeTargetEntity.Bank, "bankname"):
            case (ChangeTargetEntity.Bank, "accountname"):
                if (string.IsNullOrWhiteSpace(value)) errs.Add($"{prefix}: {field} cannot be empty.");
                else if (value.Length > 200) errs.Add($"{prefix}: {field} must be ≤ 200 characters.");
                break;

            case (ChangeTargetEntity.Contact, "email"):
                if (string.IsNullOrWhiteSpace(value) || !IsEmail(value))
                    errs.Add($"{prefix}: Email must be a valid e-mail address.");
                break;
            case (ChangeTargetEntity.Contact, "contactname"):
                if (string.IsNullOrWhiteSpace(value)) errs.Add($"{prefix}: ContactName cannot be empty.");
                break;

            case (ChangeTargetEntity.License, "issuedate"):
            case (ChangeTargetEntity.License, "expirydate"):
                if (!string.IsNullOrWhiteSpace(value) && !DateOnly.TryParse(value, out _))
                    errs.Add($"{prefix}: {field} must be a valid date (yyyy-MM-dd).");
                break;
            case (ChangeTargetEntity.License, "licensenumber"):
                if (string.IsNullOrWhiteSpace(value)) errs.Add($"{prefix}: LicenseNumber cannot be empty.");
                break;

            case (ChangeTargetEntity.Address, "addressline1"):
            case (ChangeTargetEntity.Address, "city"):
            case (ChangeTargetEntity.Address, "state"):
            case (ChangeTargetEntity.Address, "pincode"):
                if (string.IsNullOrWhiteSpace(value)) errs.Add($"{prefix}: {field} cannot be empty.");
                break;
        }
    }

    /// <summary>Validates that an Add payload parses and carries the minimum required fields for its target.</summary>
    private static void ValidateAddPayload(ChangeTargetEntity target, string payloadJson, string prefix, List<string> errs)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            root = doc.RootElement.Clone();
        }
        catch
        {
            errs.Add($"{prefix}: payloadJson is not valid JSON.");
            return;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            errs.Add($"{prefix}: payloadJson must be a JSON object.");
            return;
        }

        switch (target)
        {
            case ChangeTargetEntity.Bank:
                RequireString(root, "bankName", prefix, errs);
                RequireString(root, "bankAddress", prefix, errs);
                RequireString(root, "accountName", prefix, errs);
                RequireString(root, "accountNumber", prefix, errs);
                RequireGuid(root, "currencyId", prefix, errs);
                // IFSC format-only here (the INR-conditional requirement is asserted at apply time, needs the currency).
                if (TryGetString(root, "ifscCode", out var ifsc) && !string.IsNullOrWhiteSpace(ifsc)
                    && !SupplierBankValidationRules.IfscPattern.IsMatch(ifsc!.Trim().ToUpperInvariant()))
                    errs.Add($"{prefix}: IFSC must match ^[A-Z]{{4}}0[A-Z0-9]{{6}}$.");
                break;

            case ChangeTargetEntity.License:
                RequireString(root, "licenseNumber", prefix, errs);
                RequireString(root, "licenseType", prefix, errs);
                if (TryGetString(root, "issueDate", out var iss) && !string.IsNullOrWhiteSpace(iss) && !DateOnly.TryParse(iss, out _))
                    errs.Add($"{prefix}: issueDate must be a valid date (yyyy-MM-dd).");
                if (TryGetString(root, "expiryDate", out var exp) && !string.IsNullOrWhiteSpace(exp) && !DateOnly.TryParse(exp, out _))
                    errs.Add($"{prefix}: expiryDate must be a valid date (yyyy-MM-dd).");
                if (DateOnly.TryParse(iss, out var issD) && DateOnly.TryParse(exp, out var expD) && expD < issD)
                    errs.Add($"{prefix}: expiryDate must be on or after issueDate.");
                break;

            case ChangeTargetEntity.Address:
                RequireString(root, "addressType", prefix, errs);
                RequireString(root, "addressLine1", prefix, errs);
                RequireString(root, "city", prefix, errs);
                RequireString(root, "state", prefix, errs);
                RequireString(root, "pincode", prefix, errs);
                break;

            case ChangeTargetEntity.Contact:
                RequireString(root, "contactName", prefix, errs);
                if (TryGetString(root, "email", out var email) && (string.IsNullOrWhiteSpace(email) || !IsEmail(email!)))
                    errs.Add($"{prefix}: Email must be a valid e-mail address.");
                else if (!TryGetString(root, "email", out _))
                    errs.Add($"{prefix}: Contact requires an email.");
                break;

            case ChangeTargetEntity.Supplier:
                errs.Add($"{prefix}: the Supplier record itself cannot be Added via a change request (use Edit).");
                break;
        }
    }

    private static void RequireString(JsonElement root, string name, string prefix, List<string> errs)
    {
        if (!TryGetString(root, name, out var v) || string.IsNullOrWhiteSpace(v))
            errs.Add($"{prefix}: payloadJson.{name} is required.");
    }

    private static void RequireGuid(JsonElement root, string name, string prefix, List<string> errs)
    {
        if (!root.TryGetProperty(name, out var el)
            || el.ValueKind != JsonValueKind.String
            || !Guid.TryParse(el.GetString(), out var g)
            || g == Guid.Empty)
            errs.Add($"{prefix}: payloadJson.{name} must be a non-empty GUID.");
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString();
            return true;
        }
        return false;
    }

    private static bool IsEmail(string s)
        => !string.IsNullOrWhiteSpace(s) && s.Contains('@') && s.IndexOf('@') < s.LastIndexOf('.');
}
