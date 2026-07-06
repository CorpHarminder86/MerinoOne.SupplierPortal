using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// R9 — Supplier master input document (SupplierSync). Mirrors <see cref="Infor.SupplierOutboundPayloadBuilder"/>'s
/// child projections (addresses / contacts / bankDetails / licenses) and adds registrationStatus + erpCompany
/// for the Phase B gate (seed filter: RegistrationStatus == Approved &amp;&amp; ErpCode == null).
/// </summary>
public sealed class SupplierInputDocumentBuilder : ILnInputDocumentBuilder
{
    public string PortalEntity => LnPortalEntity.Supplier;
    public string BuilderVersion => LnInputDocumentVersions.Supplier;

    public async Task<string?> BuildJsonAsync(IAppDbContext db, Guid entityId, string transactionType, string? outboxPayloadJson, CancellationToken ct = default)
    {
        var supplier = await db.Suppliers
            .IgnoreQueryFilters()
            .Include(s => s.Addresses)
            .Include(s => s.Contacts)
            .Include(s => s.BankDetails)
            .Include(s => s.Licenses)
            .FirstOrDefaultAsync(s => s.Id == entityId && !s.IsDeleted, ct);
        if (supplier is null) return null;

        var doc = new SupplierInputDoc(
            Id: supplier.Id,
            SupplierCode: supplier.SupplierCode,
            ErpCode: supplier.ErpCode,
            ErpCompany: supplier.ErpCompany,
            Name: supplier.LegalName,
            TradeName: supplier.TradeName,
            GstNumber: supplier.GstNumber,
            PanNumber: supplier.PanNumber,
            IsActive: supplier.IsActiveSupplier,
            PaymentTermCode: supplier.PaymentTermCode,
            DeliveryTermCode: supplier.DeliveryTermCode,
            PoResponseMode: supplier.PoConfirmationMode.ToString(),
            RegistrationStatus: supplier.RegistrationStatus.ToString(),
            Addresses: supplier.Addresses.Where(a => !a.IsDeleted).Select(a => new SupplierAddressInputDoc(
                a.AddressType, a.AddressLine1, a.AddressLine2, a.Area, a.City, a.State, a.Pincode, a.Country, a.ErpCode)).ToList(),
            Contacts: supplier.Contacts.Where(c => !c.IsDeleted).Select(c => new SupplierContactInputDoc(
                c.ContactName, c.Designation, c.Email, c.Phone, c.IsPrimary, c.AddressId, c.ErpCode)).ToList(),
            BankDetails: supplier.BankDetails.Where(b => !b.IsDeleted).Select(b => new SupplierBankInputDoc(
                b.BankName, b.BankAddress, b.AccountName, b.AccountNumber, b.IfscCode, b.SwiftCode, b.IsPrimary, b.ErpCode)).ToList(),
            Licenses: supplier.Licenses.Where(l => !l.IsDeleted).Select(l => new SupplierLicenseInputDoc(
                l.LicenseNumber, l.LicenseType, l.Remarks, l.IssueDate?.ToString("yyyy-MM-dd"), l.ExpiryDate?.ToString("yyyy-MM-dd"), l.ErpCode)).ToList());

        return LnJson.SerializeInputDoc(doc);
    }
}
