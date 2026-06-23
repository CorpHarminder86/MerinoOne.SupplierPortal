using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Shared builder for the outbound Supplier-change-request→ERP request body. BOTH
/// <see cref="LiveInforIntegrationService"/> (for the HTTP POST body) and <see cref="MockInforIntegrationService"/>
/// (so dev gets the identical canonical "what we sent" payload) call this so the JSON persisted to
/// <c>InforSyncLog.PayloadJson</c> is byte-for-byte the same in Mock and Live. The shape, the per-entity end-state
/// projection (deduped by target), and serializer options mirror exactly what the Live supplier-change post builds —
/// keep them in lock-step.
///
/// R4 Module 2 — pushes an APPROVED supplier change request to LN. Per the plan, it sends the FULL intended end-state
/// per erpCode-keyed entity (the live row after the deltas were applied) — NOT a since-last delta — so LN can upsert
/// each entity by its erpCode. The change request's lines tell us WHICH live rows changed; we resolve each to its
/// current state and project it into the payload.
/// </summary>
internal static class SupplierChangeOutboundPayloadBuilder
{
    /// <summary>Serializer options shared with the Live POST body: <c>WhenWritingNull</c> drops empty fields.</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds and serializes the outbound supplier-change payload — or <c>null</c> if the change request (or its
    /// supplier) does not exist. The returned object is also what the Live service POSTs, so the two never drift.
    /// </summary>
    internal static async Task<string?> BuildJsonAsync(IAppDbContext db, Guid changeRequestId, CancellationToken ct = default)
    {
        var payload = await BuildPayloadAsync(db, changeRequestId, ct);
        return payload is null ? null : JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>
    /// Builds the anonymous supplier-change payload object (the full intended end-state per erpCode-keyed entity the
    /// change touched), or <c>null</c> when the change request or its supplier is not found. Live serializes this for
    /// the POST body; the JSON is also persisted for the SyncLog viewer.
    /// </summary>
    internal static async Task<object?> BuildPayloadAsync(IAppDbContext db, Guid changeRequestId, CancellationToken ct = default)
    {
        // IgnoreQueryFilters: this runs in the background OutboxDispatcher scope, which has NO ambient tenant/seccode
        // (the dispatcher reads everything with IgnoreQueryFilters), so the tenant/company global filters would
        // otherwise return null. We re-apply the soft-delete guard explicitly (root + children) since it's dropped too.
        var cr = await db.SupplierChangeRequests
            .IgnoreQueryFilters()
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == changeRequestId && !r.IsDeleted, ct);
        if (cr is null) return null;

        var supplier = await db.Suppliers
            .IgnoreQueryFilters()
            .Include(s => s.Addresses)
            .Include(s => s.Contacts)
            .Include(s => s.BankDetails)
            .Include(s => s.Licenses)
            .FirstOrDefaultAsync(s => s.Id == cr.SupplierId && !s.IsDeleted, ct);
        if (supplier is null) return null;

        // Build the full intended end-state per erpCode-keyed entity that the change touched. We dedupe by entity so
        // multiple field-level Edit lines on the same row collapse to one end-state object. Deletes carry the row's
        // erpCode + a delete flag so LN can retire the matching record.
        var entities = new List<object>();
        var seen = new HashSet<string>(); // "<target>:<id>" dedupe key

        foreach (var line in cr.Lines.Where(l => !l.IsDeleted))
        {
            var dedupe = $"{line.TargetEntity}:{line.TargetEntityId}";
            switch (line.TargetEntity)
            {
                case ChangeTargetEntity.Supplier:
                    if (seen.Add("Supplier:self"))
                        entities.Add(new
                        {
                            EntityType = "Supplier",
                            Operation = line.Operation.ToString(),
                            ErpCode = supplier.ErpCode,
                            supplier.LegalName,
                            supplier.TradeName,
                            supplier.GstNumber,
                            supplier.PanNumber,
                            supplier.MsmeRegNumber,
                            supplier.MsmeCategory,
                            supplier.Website,
                        });
                    break;

                case ChangeTargetEntity.Address:
                {
                    if (!seen.Add(dedupe)) break;
                    var a = supplier.Addresses.FirstOrDefault(x => x.Id == line.TargetEntityId);
                    entities.Add(new
                    {
                        EntityType = "Address",
                        Operation = line.Operation.ToString(),
                        ErpCode = a?.ErpCode,
                        a?.AddressType, a?.AddressLine1, a?.AddressLine2, a?.Area,
                        a?.City, a?.State, a?.Pincode, a?.Country,
                        Deleted = a is null || a.IsDeleted,
                    });
                    break;
                }

                case ChangeTargetEntity.Contact:
                {
                    if (!seen.Add(dedupe)) break;
                    var c = supplier.Contacts.FirstOrDefault(x => x.Id == line.TargetEntityId);
                    entities.Add(new
                    {
                        EntityType = "Contact",
                        Operation = line.Operation.ToString(),
                        ErpCode = c?.ErpCode,
                        c?.ContactName, c?.Designation, c?.Email, c?.Phone, IsPrimary = c?.IsPrimary,
                        Deleted = c is null || c.IsDeleted,
                    });
                    break;
                }

                case ChangeTargetEntity.Bank:
                {
                    if (!seen.Add(dedupe)) break;
                    var b = supplier.BankDetails.FirstOrDefault(x => x.Id == line.TargetEntityId);
                    entities.Add(new
                    {
                        EntityType = "Bank",
                        Operation = line.Operation.ToString(),
                        ErpCode = b?.ErpCode,
                        b?.BankName, b?.BankAddress, b?.AccountName, b?.AccountNumber,
                        b?.IfscCode, b?.SwiftCode, IsPrimary = b?.IsPrimary,
                        Deleted = b is null || b.IsDeleted,
                    });
                    break;
                }

                case ChangeTargetEntity.License:
                {
                    if (!seen.Add(dedupe)) break;
                    var l = supplier.Licenses.FirstOrDefault(x => x.Id == line.TargetEntityId);
                    entities.Add(new
                    {
                        EntityType = "License",
                        Operation = line.Operation.ToString(),
                        ErpCode = l?.ErpCode,
                        l?.LicenseNumber, l?.LicenseType, l?.Remarks,
                        IssueDate = l?.IssueDate?.ToString("yyyy-MM-dd"),
                        ExpiryDate = l?.ExpiryDate?.ToString("yyyy-MM-dd"),
                        Deleted = l is null || l.IsDeleted,
                    });
                    break;
                }
            }
        }

        return new
        {
            ChangeRequestId = cr.Id,
            SupplierCode = supplier.SupplierCode,
            SupplierErpCode = supplier.ErpCode,
            Summary = cr.Summary,
            Entities = entities,
        };
    }
}
