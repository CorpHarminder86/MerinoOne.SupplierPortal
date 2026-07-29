using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// R11.3 (2026-07-29) — resolves the LN logistic company (<c>TenantEntity.Code</c>) for the DOCUMENT an
/// outbound post is about, so <c>X-Infor-LnCompany</c> can be per-document instead of tenant-wide.
///
/// <para><b>The defect this fixes:</b> both LN transports set the header from <c>conn.PrimaryCompany</c> — the
/// FIRST entry of a tenant-wide comma-separated company list. In a multi-company tenant an ASN for company
/// 3000 posted with body <c>CompanyCode: "3000"</c> but header <c>X-Infor-LnCompany: "2000"</c> — and LN routes
/// on the header, so that is a wrong-company write. Every payload that carries no body company at all (invoice,
/// PO responses, supplier sync, negotiation) went out under the first-listed company unconditionally.</para>
///
/// <para>Company comes from the entity's <c>TenantEntityId</c> → <c>TenantEntity.Code</c> — the same value LN
/// sends inbound as <c>companyCode</c> and acks back as <c>erpCompany</c> (user-confirmed identical,
/// 2026-07-29). A null resolution (tenant-level document, unknown entity kind) falls back to
/// <c>conn.PrimaryCompany</c> at the transport, i.e. the old behaviour.</para>
///
/// <para>Queries use <c>IgnoreQueryFilters</c>: dispatch runs in background workers with no ambient
/// tenant/company scope.</para>
/// </summary>
internal static class LnDocumentCompanyResolver
{
    /// <summary>Company code for (<paramref name="entityName"/>, <paramref name="entityId"/>), or null.</summary>
    public static async Task<string?> ResolveAsync(IAppDbContext db, string entityName, Guid entityId, CancellationToken ct = default)
    {
        Guid? companyId = entityName switch
        {
            OutboxEntity.Asn => await db.Asns.IgnoreQueryFilters()
                .Where(a => a.Id == entityId).Select(a => a.TenantEntityId).FirstOrDefaultAsync(ct),
            OutboxEntity.Invoice => await db.Invoices.IgnoreQueryFilters()
                .Where(i => i.Id == entityId).Select(i => i.TenantEntityId).FirstOrDefaultAsync(ct),
            OutboxEntity.PurchaseOrder => await db.PurchaseOrders.IgnoreQueryFilters()
                .Where(p => p.Id == entityId).Select(p => p.TenantEntityId).FirstOrDefaultAsync(ct),
            OutboxEntity.Supplier => await db.Suppliers.IgnoreQueryFilters()
                .Where(s => s.Id == entityId).Select(s => s.TenantEntityId).FirstOrDefaultAsync(ct),
            OutboxEntity.SupplierChange => await db.SupplierChangeRequests.IgnoreQueryFilters()
                .Where(s => s.Id == entityId).Select(s => s.TenantEntityId).FirstOrDefaultAsync(ct),
            // LnPortalEntity.PoNegotiation — the dynamic route's portalEntity string; no OutboxEntity twin.
            // R12: resolved through the negotiation's PURCHASE ORDER, not the negotiation's own TenantEntityId.
            // The PO is what PoNegotiationInputDocumentBuilder puts in the body's CompanyCode, and LN's PO_Update
            // routes on that body value — so if the two ever disagreed (a negotiation stamped with a different
            // company than its PO) the header would say one company and the body another.
            "PoNegotiation" => await db.PurchaseOrderNegotiations.IgnoreQueryFilters()
                .Where(n => n.Id == entityId)
                .Join(db.PurchaseOrders.IgnoreQueryFilters().Where(p => !p.IsDeleted),
                    n => n.PurchaseOrderId, p => p.Id, (_, p) => p.TenantEntityId)
                .FirstOrDefaultAsync(ct),
            _ => null,
        };

        if (companyId is not { } cid) return null;

        return await db.TenantEntities.IgnoreQueryFilters()
            .Where(t => t.Id == cid && !t.IsDeleted)
            .Select(t => t.Code)
            .FirstOrDefaultAsync(ct);
    }
}
