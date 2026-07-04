using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Common.Documents;

/// <summary>
/// 2026-07-05 — shared owner→supplier resolution for the Documents register and the IDM sync log, both of which
/// need an optional "supplier" filter/column on top of rows whose owner is Asn/Invoice/SupplierLicense/Supplier
/// (never a direct SupplierId column). Each sub-query runs under the caller's normal RLS filters — intentionally
/// NOT IgnoreQueryFilters — because the result only narrows an already RLS-scoped outer query; a caller can never
/// gain visibility into rows here that the outer query wouldn't already show them.
/// </summary>
public static class DocumentOwnerSupplierResolver
{
    /// <summary>The DocumentUpload ids owned — directly or via Asn/Invoice/SupplierLicense — by one supplier.</summary>
    public static async Task<HashSet<Guid>> ResolveDocumentIdsForSupplierAsync(
        IAppDbContext db, Guid supplierId, CancellationToken ct)
    {
        var asnIds = await db.Asns.Where(a => a.SupplierId == supplierId).Select(a => a.Id).ToListAsync(ct);
        var invoiceIds = await db.Invoices.Where(i => i.SupplierId == supplierId).Select(i => i.Id).ToListAsync(ct);
        var licenseIds = await db.SupplierLicenses.Where(l => l.SupplierId == supplierId).Select(l => l.Id).ToListAsync(ct);

        var ownerIds = new List<Guid>(asnIds.Count + invoiceIds.Count + licenseIds.Count + 1) { supplierId };
        ownerIds.AddRange(asnIds);
        ownerIds.AddRange(invoiceIds);
        ownerIds.AddRange(licenseIds);

        var docIds = await db.DocumentUploads
            .Where(d => ownerIds.Contains(d.OwnerEntityId))
            .Select(d => d.Id)
            .ToListAsync(ct);
        return docIds.ToHashSet();
    }

    /// <summary>
    /// Batch-resolves a page of (ownerEntityType, ownerEntityId) pairs to the owning supplier's (code, name) for
    /// display. Pairs whose owner type has no supplier (Staging/PendingInvite) or that don't resolve are absent
    /// from the result — callers should fall back to blank/dash.
    /// </summary>
    public static async Task<Dictionary<(string Type, Guid Id), (string Code, string Name)>> ResolveSupplierDisplayAsync(
        IAppDbContext db, IEnumerable<(string Type, Guid Id)> owners, CancellationToken ct)
    {
        var pairs = owners.Distinct().ToList();
        var result = new Dictionary<(string, Guid), (string, string)>();
        if (pairs.Count == 0) return result;

        var ownerToSupplier = new Dictionary<(string, Guid), Guid>();
        foreach (var p in pairs.Where(p => p.Type == DocumentOwnerTypes.Supplier))
            ownerToSupplier[p] = p.Id;

        async Task MapAsync(string type, Func<List<Guid>, Task<List<(Guid Id, Guid SupplierId)>>> fetch)
        {
            var ids = pairs.Where(p => p.Type == type).Select(p => p.Id).ToList();
            if (ids.Count == 0) return;
            foreach (var (id, supplierId) in await fetch(ids))
                ownerToSupplier[(type, id)] = supplierId;
        }

        await MapAsync(DocumentOwnerTypes.Asn, async ids => await db.Asns
            .Where(a => ids.Contains(a.Id)).Select(a => new ValueTuple<Guid, Guid>(a.Id, a.SupplierId)).ToListAsync(ct));

        await MapAsync(DocumentOwnerTypes.Invoice, async ids => await db.Invoices
            .Where(i => ids.Contains(i.Id)).Select(i => new ValueTuple<Guid, Guid>(i.Id, i.SupplierId)).ToListAsync(ct));

        await MapAsync(DocumentOwnerTypes.SupplierLicense, async ids => await db.SupplierLicenses
            .Where(l => ids.Contains(l.Id)).Select(l => new ValueTuple<Guid, Guid>(l.Id, l.SupplierId)).ToListAsync(ct));

        var supplierIds = ownerToSupplier.Values.Distinct().ToList();
        if (supplierIds.Count == 0) return result;

        var suppliers = await db.Suppliers
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.SupplierCode, s.LegalName })
            .ToDictionaryAsync(s => s.Id, ct);

        foreach (var (owner, supplierId) in ownerToSupplier)
            if (suppliers.TryGetValue(supplierId, out var sup))
                result[owner] = (sup.SupplierCode, sup.LegalName);

        return result;
    }
}
