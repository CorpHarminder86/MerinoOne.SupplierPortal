using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Shared builder for the outbound Supplier (onboarding / sync)→ERP request body. BOTH
/// <see cref="LiveInforIntegrationService"/> (for the HTTP POST body) and <see cref="MockInforIntegrationService"/>
/// (so dev gets the identical canonical "what we sent" payload) call this so the JSON persisted to
/// <c>InforSyncLog.PayloadJson</c> is byte-for-byte the same in Mock and Live. The shape, field map (incl. the
/// addresses[]/contacts[]/bankDetails[]/licenses[] child projections), and serializer options mirror exactly what the
/// Live supplier sync builds — keep them in lock-step.
///
/// TODO (per-tenant Infor LN spec): the LN supplier (business partner) field map — including the child collection
/// field names — is a STARTER. Confirm with the Infor LN team before enabling Mode=Live.
/// </summary>
internal static class SupplierOutboundPayloadBuilder
{
    /// <summary>Serializer options shared with the Live POST body: <c>WhenWritingNull</c> drops empty fields.</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds and serializes the outbound supplier payload — or <c>null</c> if the supplier does not exist. The
    /// returned object is also what the Live service POSTs, so the two never drift.
    /// </summary>
    internal static async Task<string?> BuildJsonAsync(IAppDbContext db, Guid supplierId, CancellationToken ct = default)
    {
        var payload = await BuildPayloadAsync(db, supplierId, ct);
        return payload is null ? null : JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>
    /// Builds the anonymous supplier payload object (header + addresses[]/contacts[]/bankDetails[]/licenses[]), or
    /// <c>null</c> when the supplier is not found. Live serializes this for the POST body; the JSON is also persisted
    /// for the SyncLog viewer.
    /// </summary>
    internal static async Task<object?> BuildPayloadAsync(IAppDbContext db, Guid supplierId, CancellationToken ct = default)
    {
        // IgnoreQueryFilters: this runs in the background OutboxDispatcher scope, which has NO ambient tenant/seccode
        // (the dispatcher reads everything with IgnoreQueryFilters), so the tenant/company global filters would
        // otherwise return null. We re-apply the soft-delete guard explicitly (root + children, the children in the
        // in-memory projection) since it's dropped too.
        var supplier = await db.Suppliers
            .IgnoreQueryFilters()
            .Include(s => s.Addresses)
            .Include(s => s.Contacts)
            .Include(s => s.BankDetails)
            .Include(s => s.Licenses)
            .FirstOrDefaultAsync(s => s.Id == supplierId && !s.IsDeleted, ct);
        if (supplier is null) return null;

        // R4 Module 1 — extended supplier payload: carries addresses, contacts, bank details, licenses,
        // term/currency codes, poResponseMode and erpCode. TODO: confirm the real LN supplier (business partner)
        // field map — including the addresses[] / contacts[] child field names.
        return new
        {
            SupplierCode = supplier.SupplierCode,
            ErpCode = supplier.ErpCode,
            Name = supplier.LegalName,
            TradeName = supplier.TradeName,
            GstNumber = supplier.GstNumber,
            PanNumber = supplier.PanNumber,
            IsActive = supplier.IsActiveSupplier,
            PaymentTermCode = supplier.PaymentTermCode,
            DeliveryTermCode = supplier.DeliveryTermCode,
            // R4 (2026-06-26) — D1: PoConfirmationMode (was PoResponseMode). Payload field name kept stable for LN.
            PoResponseMode = supplier.PoConfirmationMode.ToString(),
            Addresses = supplier.Addresses.Where(a => !a.IsDeleted).Select(a => new
            {
                a.AddressType,
                Line1 = a.AddressLine1,
                Line2 = a.AddressLine2,
                a.Area,
                a.City,
                a.State,
                Pincode = a.Pincode,
                a.Country,
                a.ErpCode,
            }).ToList(),
            Contacts = supplier.Contacts.Where(c => !c.IsDeleted).Select(c => new
            {
                c.ContactName,
                c.Designation,
                c.Email,
                c.Phone,
                c.IsPrimary,
                c.AddressId,
                c.ErpCode,
            }).ToList(),
            BankDetails = supplier.BankDetails.Where(b => !b.IsDeleted).Select(b => new
            {
                b.BankName,
                b.BankAddress,
                b.AccountName,
                b.AccountNumber,
                b.IfscCode,
                b.SwiftCode,
                b.IsPrimary,
                b.ErpCode,
            }).ToList(),
            Licenses = supplier.Licenses.Where(l => !l.IsDeleted).Select(l => new
            {
                l.LicenseNumber,
                l.LicenseType,
                IssueDate = l.IssueDate?.ToString("yyyy-MM-dd"),
                ExpiryDate = l.ExpiryDate?.ToString("yyyy-MM-dd"),
                l.ErpCode,
            }).ToList(),
        };
    }
}
