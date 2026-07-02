using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Invoices;

/// <summary>
/// R6 (2026-07-02) — batch tax-rate resolution for the invoice generation/edit/submit paths. Resolves a set of
/// <c>proc.Tax</c> ids to their CURRENT (rate, code, description).
///
/// <para><b>Filter discipline (plan D12):</b> <c>proc.Tax</c> is ICompanyScoped and sharing-aware — a PO line's
/// <c>taxId</c> may legitimately reference a Tax row stored under an UNSHARED source company (the inbound PO
/// snapshot resolves it there). The lookup therefore uses <c>IgnoreQueryFilters()</c> and re-applies
/// <c>!IsDeleted</c> plus an explicit <c>TenantId</c> guard (the owning entity's tenant) so no cross-tenant row
/// ever resolves.</para>
///
/// <para>A NULL <see cref="ResolvedTax.Rate"/> is DATA, not an error — the caller decides (generation blocks the
/// ASN; submit rejects; an explicit reselect rejects).</para>
/// </summary>
public sealed class TaxRateResolver
{
    public sealed record ResolvedTax(decimal? Rate, string Code, string Description);

    private readonly IAppDbContext _db;
    public TaxRateResolver(IAppDbContext db) => _db = db;

    /// <summary>
    /// Resolves the distinct <paramref name="taxIds"/> to (rate, code, description). Missing ids are simply
    /// absent from the dictionary (deleted row / wrong tenant). <paramref name="tenantId"/> is the OWNING
    /// entity's tenant (asn/invoice); when null (legacy unscoped rows) the tenant guard is skipped.
    /// </summary>
    public async Task<Dictionary<Guid, ResolvedTax>> ResolveAsync(
        IEnumerable<Guid> taxIds, Guid? tenantId, CancellationToken ct)
    {
        var ids = taxIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, ResolvedTax>();

        var q = _db.Taxes.IgnoreQueryFilters().Where(t => ids.Contains(t.Id) && !t.IsDeleted);
        if (tenantId.HasValue)
            q = q.Where(t => t.TenantId == tenantId.Value);

        var rows = await q.Select(t => new { t.Id, t.TaxRate, t.Code, t.Description }).ToListAsync(ct);
        return rows.ToDictionary(t => t.Id, t => new ResolvedTax(t.TaxRate, t.Code, t.Description));
    }
}
