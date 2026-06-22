using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Suppliers.Commands;
using MerinoOne.SupplierPortal.Domain.Entities.Supplier;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Suppliers.ChangeRequests;

/// <summary>
/// Typed, explicit (NOT reflection) per-target appliers invoked inside the Approve transaction — once per
/// change-request line, BEFORE the single <c>SaveChangesAsync</c>. Each maps a line
/// <c>(operation, targetEntityId, fieldName/newValue/payloadJson)</c> onto the live supplier row:
/// <list type="bullet">
///   <item><c>Add</c> — inserts a new child row from <c>payloadJson</c> (stamping the supplier's G-seccode + audit);</item>
///   <item><c>Edit</c> — patches the named allow-listed field on the existing row (revalidating the value);</item>
///   <item><c>Delete</c> — soft-deletes the existing row.</item>
/// </list>
/// Mutations are tracked in the caller's change tracker and committed by the caller's single SaveChanges, so the
/// whole approval (all lines + the request state flip) is one atomic transaction. The seccode for any inserted
/// row is the supplier's G-seccode (the same <c>Owner</c> module-1 commands stamp), keeping RLS intact.
///
/// Scoped: ctor-injects the per-request <see cref="IAppDbContext"/> / <see cref="ICurrentUser"/>.
/// </summary>
public sealed class SupplierChangeApplier
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public SupplierChangeApplier(IAppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    /// <summary>Routes a line to the matching typed applier. <paramref name="supplier"/> is already loaded + tracked.</summary>
    public async Task ApplyLineAsync(
        Domain.Entities.Supplier.Supplier supplier,
        SupplierChangeRequestLine line,
        DateTime now,
        CancellationToken ct)
    {
        switch (line.TargetEntity)
        {
            case ChangeTargetEntity.Supplier: await ApplySupplierScalarDeltaAsync(supplier, line, now, ct); break;
            case ChangeTargetEntity.Address:  await ApplyAddressDeltaAsync(supplier, line, now, ct); break;
            case ChangeTargetEntity.Contact:  await ApplyContactDeltaAsync(supplier, line, now, ct); break;
            case ChangeTargetEntity.Bank:     await ApplyBankDeltaAsync(supplier, line, now, ct); break;
            case ChangeTargetEntity.License:  await ApplyLicenseDeltaAsync(supplier, line, now, ct); break;
            default: throw new ValidationException(Error($"Unknown target entity '{line.TargetEntity}'."));
        }
    }

    // ── Supplier scalar (Edit only) ──────────────────────────────────────────────────────────────
    public Task ApplySupplierScalarDeltaAsync(Domain.Entities.Supplier.Supplier s, SupplierChangeRequestLine line, DateTime now, CancellationToken ct)
    {
        if (line.Operation != ChangeOperation.Edit)
            throw new ValidationException(Error("Supplier supports Edit only."));

        var field = line.FieldName!;
        AssertEditable(ChangeTargetEntity.Supplier, field);
        var v = Norm(line.NewValue);

        switch (field.ToLowerInvariant())
        {
            case "legalname":      s.LegalName = v ?? s.LegalName; break;
            case "tradename":      s.TradeName = v; break;
            case "website":        s.Website = v; break;
            case "gstnumber":      s.GstNumber = v?.ToUpperInvariant(); break;
            case "pannumber":      s.PanNumber = v?.ToUpperInvariant(); break;
            case "msmeregnumber":  s.MsmeRegNumber = v; break;
            case "msmecategory":   s.MsmeCategory = v; break;
            default: throw new ValidationException(Error($"'{field}' is not an editable Supplier field."));
        }
        Touch(s, now);
        return Task.CompletedTask;
    }

    // ── Address (Add / Edit / Delete) ────────────────────────────────────────────────────────────
    public async Task ApplyAddressDeltaAsync(Domain.Entities.Supplier.Supplier s, SupplierChangeRequestLine line, DateTime now, CancellationToken ct)
    {
        switch (line.Operation)
        {
            case ChangeOperation.Add:
            {
                var p = Payload(line);
                var entity = new SupplierAddress
                {
                    Id = Guid.NewGuid(),
                    SupplierId = s.Id,
                    AddressType = Get(p, "addressType") ?? "Other",
                    AddressLine1 = Get(p, "addressLine1") ?? string.Empty,
                    AddressLine2 = Get(p, "addressLine2"),
                    Area = Get(p, "area"),
                    City = Get(p, "city") ?? string.Empty,
                    State = Get(p, "state") ?? string.Empty,
                    Pincode = Get(p, "pincode") ?? string.Empty,
                    Country = Get(p, "country") ?? "India",
                    CreatedBy = Actor(),
                    CreatedOn = now,
                };
                _db.SupplierAddresses.Add(entity);
                break;
            }
            case ChangeOperation.Edit:
            {
                var a = await _db.SupplierAddresses.FirstOrDefaultAsync(x => x.Id == line.TargetEntityId && x.SupplierId == s.Id, ct)
                        ?? throw new NotFoundException("SupplierAddress", line.TargetEntityId ?? Guid.Empty);
                var field = line.FieldName!;
                AssertEditable(ChangeTargetEntity.Address, field);
                var v = Norm(line.NewValue);
                switch (field.ToLowerInvariant())
                {
                    case "addresstype":  a.AddressType = v ?? a.AddressType; break;
                    case "addressline1": a.AddressLine1 = v ?? a.AddressLine1; break;
                    case "addressline2": a.AddressLine2 = v; break;
                    case "area":         a.Area = v; break;
                    case "city":         a.City = v ?? a.City; break;
                    case "state":        a.State = v ?? a.State; break;
                    case "pincode":      a.Pincode = v ?? a.Pincode; break;
                    case "country":      a.Country = v ?? a.Country; break;
                    default: throw new ValidationException(Error($"'{field}' is not an editable Address field."));
                }
                Touch(a, now);
                break;
            }
            case ChangeOperation.Delete:
            {
                var a = await _db.SupplierAddresses.FirstOrDefaultAsync(x => x.Id == line.TargetEntityId && x.SupplierId == s.Id, ct)
                        ?? throw new NotFoundException("SupplierAddress", line.TargetEntityId ?? Guid.Empty);
                _db.SupplierAddresses.Remove(a);   // soft-delete via the audit interceptor (stamps IsDeleted/DeletedBy/On).
                break;
            }
        }
    }

    // ── Contact (Add / Edit / Delete) ────────────────────────────────────────────────────────────
    public async Task ApplyContactDeltaAsync(Domain.Entities.Supplier.Supplier s, SupplierChangeRequestLine line, DateTime now, CancellationToken ct)
    {
        switch (line.Operation)
        {
            case ChangeOperation.Add:
            {
                var p = Payload(line);
                var entity = new SupplierContact
                {
                    Id = Guid.NewGuid(),
                    SupplierId = s.Id,
                    ContactName = Get(p, "contactName") ?? string.Empty,
                    Designation = Get(p, "designation"),
                    Email = (Get(p, "email") ?? string.Empty).ToLowerInvariant(),
                    Phone = Get(p, "phone"),
                    IsPrimary = GetBool(p, "isPrimary"),
                    CreatedBy = Actor(),
                    CreatedOn = now,
                };
                _db.SupplierContacts.Add(entity);
                break;
            }
            case ChangeOperation.Edit:
            {
                var c = await _db.SupplierContacts.FirstOrDefaultAsync(x => x.Id == line.TargetEntityId && x.SupplierId == s.Id, ct)
                        ?? throw new NotFoundException("SupplierContact", line.TargetEntityId ?? Guid.Empty);
                var field = line.FieldName!;
                AssertEditable(ChangeTargetEntity.Contact, field);
                var v = Norm(line.NewValue);
                switch (field.ToLowerInvariant())
                {
                    case "contactname": c.ContactName = v ?? c.ContactName; break;
                    case "designation": c.Designation = v; break;
                    case "email":       c.Email = (v ?? c.Email).ToLowerInvariant(); break;
                    case "phone":       c.Phone = v; break;
                    case "isprimary":   c.IsPrimary = ParseBool(v); break;
                    default: throw new ValidationException(Error($"'{field}' is not an editable Contact field."));
                }
                Touch(c, now);
                break;
            }
            case ChangeOperation.Delete:
            {
                var c = await _db.SupplierContacts.FirstOrDefaultAsync(x => x.Id == line.TargetEntityId && x.SupplierId == s.Id, ct)
                        ?? throw new NotFoundException("SupplierContact", line.TargetEntityId ?? Guid.Empty);
                _db.SupplierContacts.Remove(c);   // soft-delete via the audit interceptor.
                break;
            }
        }
    }

    // ── Bank (Add / Edit / Delete) — BaseAggregateRoot, stamp the supplier's G-seccode ─────────────
    public async Task ApplyBankDeltaAsync(Domain.Entities.Supplier.Supplier s, SupplierChangeRequestLine line, DateTime now, CancellationToken ct)
    {
        switch (line.Operation)
        {
            case ChangeOperation.Add:
            {
                var p = Payload(line);
                var currencyId = GetGuid(p, "currencyId")
                    ?? throw new ValidationException(Error("payloadJson.currencyId is required for a bank Add."));
                // Reuse module 1's INR-conditional IFSC rule (needs the currency code).
                var currency = await _db.Currencies.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == currencyId, ct)
                    ?? throw new ValidationException(Error("Currency not found for the bank Add."));
                var ifsc = Get(p, "ifscCode");
                SupplierBankValidationRules.AssertIfscConditional(currency.Code, ifsc);

                var entity = new SupplierBankDetail
                {
                    Id = Guid.NewGuid(),
                    SupplierId = s.Id,
                    BankName = Get(p, "bankName") ?? string.Empty,
                    BankAddress = Get(p, "bankAddress") ?? string.Empty,
                    AccountName = Get(p, "accountName") ?? string.Empty,
                    AccountNumber = Get(p, "accountNumber") ?? string.Empty,
                    CurrencyId = currencyId,
                    IfscCode = (ifsc ?? string.Empty).Trim().ToUpperInvariant(),
                    SwiftCode = string.IsNullOrWhiteSpace(Get(p, "swiftCode")) ? null : Get(p, "swiftCode")!.Trim().ToUpperInvariant(),
                    IsPrimary = GetBool(p, "isPrimary"),
                    SeccodeId = s.SeccodeId,   // Owner = supplier's G-seccode (seccode RLS), same as module-1 add.
                    CreatedBy = Actor(),
                    CreatedOn = now,
                };
                if (entity.IsPrimary)
                    await DemoteOtherPrimaryBanksAsync(s.Id, entity.Id, ct);
                _db.SupplierBankDetails.Add(entity);
                break;
            }
            case ChangeOperation.Edit:
            {
                var b = await _db.SupplierBankDetails.FirstOrDefaultAsync(x => x.Id == line.TargetEntityId && x.SupplierId == s.Id, ct)
                        ?? throw new NotFoundException("SupplierBankDetail", line.TargetEntityId ?? Guid.Empty);
                var field = line.FieldName!;
                AssertEditable(ChangeTargetEntity.Bank, field);
                var v = Norm(line.NewValue);
                switch (field.ToLowerInvariant())
                {
                    case "bankname":      b.BankName = v ?? b.BankName; break;
                    case "bankaddress":   b.BankAddress = v ?? b.BankAddress; break;
                    case "accountname":   b.AccountName = v ?? b.AccountName; break;
                    case "accountnumber": b.AccountNumber = v ?? b.AccountNumber; break;
                    case "ifsccode":
                    {
                        var currency = await _db.Currencies.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == b.CurrencyId, ct);
                        SupplierBankValidationRules.AssertIfscConditional(currency?.Code, v);
                        b.IfscCode = (v ?? string.Empty).Trim().ToUpperInvariant();
                        break;
                    }
                    case "swiftcode":  b.SwiftCode = string.IsNullOrWhiteSpace(v) ? null : v!.Trim().ToUpperInvariant(); break;
                    case "isprimary":
                        var primary = ParseBool(v);
                        if (primary && !b.IsPrimary) await DemoteOtherPrimaryBanksAsync(s.Id, b.Id, ct);
                        b.IsPrimary = primary;
                        break;
                    default: throw new ValidationException(Error($"'{field}' is not an editable Bank field."));
                }
                Touch(b, now);
                break;
            }
            case ChangeOperation.Delete:
            {
                var b = await _db.SupplierBankDetails.FirstOrDefaultAsync(x => x.Id == line.TargetEntityId && x.SupplierId == s.Id, ct)
                        ?? throw new NotFoundException("SupplierBankDetail", line.TargetEntityId ?? Guid.Empty);
                _db.SupplierBankDetails.Remove(b);   // soft-delete via the audit interceptor.
                break;
            }
        }
    }

    // ── License (Add / Edit / Delete) — BaseAggregateRoot, stamp the supplier's G-seccode ──────────
    public async Task ApplyLicenseDeltaAsync(Domain.Entities.Supplier.Supplier s, SupplierChangeRequestLine line, DateTime now, CancellationToken ct)
    {
        switch (line.Operation)
        {
            case ChangeOperation.Add:
            {
                var p = Payload(line);
                var issue = GetDate(p, "issueDate");
                var expiry = GetDate(p, "expiryDate");
                if (issue.HasValue && expiry.HasValue && expiry.Value < issue.Value)
                    throw new ValidationException(Error("expiryDate must be on or after issueDate."));

                var entity = new SupplierLicense
                {
                    Id = Guid.NewGuid(),
                    SupplierId = s.Id,
                    LicenseNumber = Get(p, "licenseNumber") ?? string.Empty,
                    LicenseType = Get(p, "licenseType") ?? string.Empty,
                    Remarks = Get(p, "remarks"),
                    IssueDate = issue,
                    ExpiryDate = expiry,
                    SeccodeId = s.SeccodeId,   // Owner = supplier's G-seccode.
                    CreatedBy = Actor(),
                    CreatedOn = now,
                };
                _db.SupplierLicenses.Add(entity);
                break;
            }
            case ChangeOperation.Edit:
            {
                var l = await _db.SupplierLicenses.FirstOrDefaultAsync(x => x.Id == line.TargetEntityId && x.SupplierId == s.Id, ct)
                        ?? throw new NotFoundException("SupplierLicense", line.TargetEntityId ?? Guid.Empty);
                var field = line.FieldName!;
                AssertEditable(ChangeTargetEntity.License, field);
                var v = Norm(line.NewValue);
                switch (field.ToLowerInvariant())
                {
                    case "licensenumber": l.LicenseNumber = v ?? l.LicenseNumber; break;
                    case "licensetype":   l.LicenseType = v ?? l.LicenseType; break;
                    case "remarks":       l.Remarks = v; break;
                    case "issuedate":     l.IssueDate = ParseDate(v); break;
                    case "expirydate":    l.ExpiryDate = ParseDate(v); break;
                    default: throw new ValidationException(Error($"'{field}' is not an editable License field."));
                }
                if (l.IssueDate.HasValue && l.ExpiryDate.HasValue && l.ExpiryDate.Value < l.IssueDate.Value)
                    throw new ValidationException(Error("expiryDate must be on or after issueDate."));
                Touch(l, now);
                break;
            }
            case ChangeOperation.Delete:
            {
                var l = await _db.SupplierLicenses.FirstOrDefaultAsync(x => x.Id == line.TargetEntityId && x.SupplierId == s.Id, ct)
                        ?? throw new NotFoundException("SupplierLicense", line.TargetEntityId ?? Guid.Empty);
                _db.SupplierLicenses.Remove(l);   // soft-delete via the audit interceptor.
                break;
            }
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────
    private async Task DemoteOtherPrimaryBanksAsync(Guid supplierId, Guid keepId, CancellationToken ct)
    {
        var others = await _db.SupplierBankDetails
            .Where(b => b.SupplierId == supplierId && b.Id != keepId && b.IsPrimary)
            .ToListAsync(ct);
        foreach (var o in others) o.IsPrimary = false;
    }

    private void AssertEditable(ChangeTargetEntity target, string field)
    {
        if (!SupplierChangeFieldCatalog.IsEditableField(target, field))
            throw new ValidationException(Error($"'{field}' is not an editable {target} field."));
    }

    private string Actor() => string.IsNullOrEmpty(_user.UserCode) ? "system" : _user.UserCode;

    private void Touch(Domain.Common.AuditableEntity e, DateTime now)
    {
        e.UpdatedBy = Actor();
        e.UpdatedOn = now;
    }

    private static JsonElement Payload(SupplierChangeRequestLine line)
    {
        if (string.IsNullOrWhiteSpace(line.PayloadJson))
            throw new ValidationException(Error("Add operation requires a payloadJson."));
        try
        {
            using var doc = JsonDocument.Parse(line.PayloadJson);
            return doc.RootElement.Clone();
        }
        catch
        {
            throw new ValidationException(Error("payloadJson is not valid JSON."));
        }
    }

    private static string? Norm(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string? Get(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? Norm(el.GetString()) : null;

    private static bool GetBool(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) && b,
            _ => false
        };
    }

    private static Guid? GetGuid(JsonElement p, string name)
        => Get(p, name) is { } s && Guid.TryParse(s, out var g) ? g : null;

    private static DateOnly? GetDate(JsonElement p, string name)
        => Get(p, name) is { } s && DateOnly.TryParse(s, out var d) ? d : null;

    private static bool ParseBool(string? v) => bool.TryParse(v, out var b) && b;
    private static DateOnly? ParseDate(string? v) => DateOnly.TryParse(v, out var d) ? d : (DateOnly?)null;

    private static IReadOnlyDictionary<string, string[]> Error(string msg)
        => new Dictionary<string, string[]> { ["lines"] = new[] { msg } };
}
