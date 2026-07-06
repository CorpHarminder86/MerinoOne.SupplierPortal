using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// R9 — SupplierChange input document. Mirrors <see cref="Infor.SupplierChangeOutboundPayloadBuilder"/>:
/// the full intended END-STATE per erpCode-keyed entity the change touched (deduped by target, deletes
/// flagged), never a delta. Fields not applicable to an entity type stay null (nulls are kept in input
/// documents); the request expression projects per <c>entityType</c>.
/// </summary>
public sealed class SupplierChangeInputDocumentBuilder : ILnInputDocumentBuilder
{
    public string PortalEntity => LnPortalEntity.SupplierChange;
    public string BuilderVersion => LnInputDocumentVersions.SupplierChange;

    public async Task<string?> BuildJsonAsync(IAppDbContext db, Guid entityId, string transactionType, string? outboxPayloadJson, CancellationToken ct = default)
    {
        var cr = await db.SupplierChangeRequests
            .IgnoreQueryFilters()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == entityId && !r.IsDeleted, ct);
        if (cr is null) return null;

        var supplier = await db.Suppliers
            .IgnoreQueryFilters()
            .Include(s => s.Addresses)
            .Include(s => s.Contacts)
            .Include(s => s.BankDetails)
            .Include(s => s.Licenses)
            .FirstOrDefaultAsync(s => s.Id == cr.SupplierId && !s.IsDeleted, ct);
        if (supplier is null) return null;

        var entities = new List<SupplierChangeEntityInputDoc>();
        var seen = new HashSet<string>(); // "<target>:<id>" dedupe key — multiple Edit lines on one row collapse

        foreach (var line in cr.Lines.Where(l => !l.IsDeleted))
        {
            var dedupe = $"{line.TargetEntity}:{line.TargetEntityId}";
            switch (line.TargetEntity)
            {
                case ChangeTargetEntity.Supplier:
                    if (seen.Add("Supplier:self"))
                        entities.Add(Blank("Supplier", line.Operation.ToString()) with
                        {
                            ErpCode = supplier.ErpCode,
                            LegalName = supplier.LegalName,
                            TradeName = supplier.TradeName,
                            GstNumber = supplier.GstNumber,
                            PanNumber = supplier.PanNumber,
                            MsmeRegNumber = supplier.MsmeRegNumber,
                            MsmeCategory = supplier.MsmeCategory,
                            Website = supplier.Website,
                        });
                    break;

                case ChangeTargetEntity.Address:
                {
                    if (!seen.Add(dedupe)) break;
                    var a = supplier.Addresses.FirstOrDefault(x => x.Id == line.TargetEntityId);
                    entities.Add(Blank("Address", line.Operation.ToString()) with
                    {
                        ErpCode = a?.ErpCode,
                        AddressType = a?.AddressType,
                        AddressLine1 = a?.AddressLine1,
                        AddressLine2 = a?.AddressLine2,
                        Area = a?.Area,
                        City = a?.City,
                        State = a?.State,
                        Pincode = a?.Pincode,
                        Country = a?.Country,
                        Deleted = a is null || a.IsDeleted,
                    });
                    break;
                }

                case ChangeTargetEntity.Contact:
                {
                    if (!seen.Add(dedupe)) break;
                    var c = supplier.Contacts.FirstOrDefault(x => x.Id == line.TargetEntityId);
                    entities.Add(Blank("Contact", line.Operation.ToString()) with
                    {
                        ErpCode = c?.ErpCode,
                        ContactName = c?.ContactName,
                        Designation = c?.Designation,
                        Email = c?.Email,
                        Phone = c?.Phone,
                        IsPrimary = c?.IsPrimary,
                        Deleted = c is null || c.IsDeleted,
                    });
                    break;
                }

                case ChangeTargetEntity.Bank:
                {
                    if (!seen.Add(dedupe)) break;
                    var b = supplier.BankDetails.FirstOrDefault(x => x.Id == line.TargetEntityId);
                    entities.Add(Blank("Bank", line.Operation.ToString()) with
                    {
                        ErpCode = b?.ErpCode,
                        BankName = b?.BankName,
                        BankAddress = b?.BankAddress,
                        AccountName = b?.AccountName,
                        AccountNumber = b?.AccountNumber,
                        IfscCode = b?.IfscCode,
                        SwiftCode = b?.SwiftCode,
                        IsPrimary = b?.IsPrimary,
                        Deleted = b is null || b.IsDeleted,
                    });
                    break;
                }

                case ChangeTargetEntity.License:
                {
                    if (!seen.Add(dedupe)) break;
                    var l = supplier.Licenses.FirstOrDefault(x => x.Id == line.TargetEntityId);
                    entities.Add(Blank("License", line.Operation.ToString()) with
                    {
                        ErpCode = l?.ErpCode,
                        LicenseNumber = l?.LicenseNumber,
                        LicenseType = l?.LicenseType,
                        Remarks = l?.Remarks,
                        IssueDate = l?.IssueDate?.ToString("yyyy-MM-dd"),
                        ExpiryDate = l?.ExpiryDate?.ToString("yyyy-MM-dd"),
                        Deleted = l is null || l.IsDeleted,
                    });
                    break;
                }
            }
        }

        var doc = new SupplierChangeInputDoc(
            ChangeRequestId: cr.Id,
            SupplierCode: supplier.SupplierCode,
            SupplierErpCode: supplier.ErpCode,
            Summary: cr.Summary,
            ChangeStatus: cr.ChangeStatus.ToString(),
            Entities: entities);

        return LnJson.SerializeInputDoc(doc);
    }

    private static SupplierChangeEntityInputDoc Blank(string entityType, string operation) => new(
        EntityType: entityType, Operation: operation, ErpCode: null, Deleted: null,
        LegalName: null, TradeName: null, GstNumber: null, PanNumber: null, MsmeRegNumber: null,
        MsmeCategory: null, Website: null,
        AddressType: null, AddressLine1: null, AddressLine2: null, Area: null, City: null, State: null,
        Pincode: null, Country: null,
        ContactName: null, Designation: null, Email: null, Phone: null, IsPrimary: null,
        BankName: null, BankAddress: null, AccountName: null, AccountNumber: null, IfscCode: null, SwiftCode: null,
        LicenseNumber: null, LicenseType: null, Remarks: null, IssueDate: null, ExpiryDate: null);
}
