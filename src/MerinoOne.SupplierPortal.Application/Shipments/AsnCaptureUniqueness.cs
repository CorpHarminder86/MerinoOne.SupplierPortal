using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R11.1 (2026-07-29) — serial / lot-number uniqueness for ASN capture.
///
/// <para><b>Scope: per ITEM, within the company.</b> A serial identifies one physical unit of one item, so
/// serial "2222" may exist once for ITM-00001 in company 3000 across every PO and every ASN — while a
/// different item may legitimately reuse the same serial string. This replaces the original per-PO scope,
/// which let the same serial be captured twice as long as the two lines sat on different POs (found in E2E:
/// one ASN carried serial 2222 on both a PO-3000 line and a PO-4000 line and passed every check).</para>
///
/// <para><b>Reservation states:</b> every ASN reserves its serials EXCEPT <see cref="AsnStatus.Cancelled"/> and
/// <see cref="AsnStatus.Rejected"/>, which release them for reuse. Drafts therefore reserve — two open drafts
/// cannot both claim unit 2222, which is the point: the alternative defers the clash to buyer approval, when
/// it is far more expensive to unwind.</para>
///
/// <para><b>Where this fires:</b> create, update AND submit. Create/update fail fast while the supplier is
/// still typing; the submit-time call remains the atomic authority, because another ASN can claim a serial in
/// the window between saving a draft and the buyer approving it.</para>
///
/// <para>Matching is by ITEM CODE, not ItemId: PO lines are ERP-fed and routinely carry a null ItemId, so
/// ItemCode is the only reliable key (the same reason the serial/lot capture resolves Item by code).</para>
/// </summary>
public static class AsnCaptureUniqueness
{
    /// <summary>One captured serial or lot, tagged with the item it belongs to.</summary>
    public readonly record struct Capture(string ItemCode, string Value);

    /// <summary>ASN statuses that do NOT reserve their captured serials/lots.</summary>
    private static bool Releases(AsnStatus s) => s is AsnStatus.Cancelled or AsnStatus.Rejected;

    /// <summary>
    /// Appends an error for every serial/lot that is either captured twice within this ASN or already held by
    /// another live ASN for the same item in the same company.
    /// </summary>
    /// <param name="excludeAsnId">The ASN being created/edited — null on create, so nothing is excluded.</param>
    public static async Task ValidateAsync(
        IAppDbContext db,
        Guid? excludeAsnId,
        Guid? companyId,
        IReadOnlyList<Capture> serials,
        IReadOnlyList<Capture> lots,
        Action<string, string> addError,
        CancellationToken ct = default)
    {
        DuplicatesWithin(serials, "Serial number", addError);
        DuplicatesWithin(lots, "Lot number", addError);

        if (companyId is null) return;   // no company scope resolvable → the submit-time guard still applies

        await ClashesElsewhereAsync(db, excludeAsnId, companyId.Value, serials, isSerial: true, addError, ct);
        await ClashesElsewhereAsync(db, excludeAsnId, companyId.Value, lots, isSerial: false, addError, ct);
    }

    /// <summary>
    /// The same value captured twice on THIS ASN for the same item — across lines, not just within one line.
    /// (<c>AsnLineRules.SerialsDistinct</c> only ever looked within a single line.)
    /// </summary>
    private static void DuplicatesWithin(IReadOnlyList<Capture> captures, string label, Action<string, string> addError)
    {
        foreach (var dup in captures
                     .GroupBy(c => (c.ItemCode, c.Value), CaptureComparer.Instance)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
            addError("lines", $"{label} '{dup.Value}' is captured more than once for item '{dup.ItemCode}' on this ASN.");
    }

    private static async Task ClashesElsewhereAsync(
        IAppDbContext db,
        Guid? excludeAsnId,
        Guid companyId,
        IReadOnlyList<Capture> captures,
        bool isSerial,
        Action<string, string> addError,
        CancellationToken ct)
    {
        if (captures.Count == 0) return;

        var values = captures.Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Project (itemCode, value, asnNumber) for every LIVE capture of these values in this company, then match
        // on the item in memory — SQL-side pair matching would need a temp table for no benefit at these sizes.
        var existing = isSerial
            ? await (from s in db.AsnLineSerials
                     join al in db.AsnLines on s.AsnLineId equals al.Id
                     join a in db.Asns on al.AsnId equals a.Id
                     join pol in db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                     where !s.IsDeleted && !al.IsDeleted && !a.IsDeleted
                           && a.TenantEntityId == companyId
                           && (excludeAsnId == null || a.Id != excludeAsnId)
                           && a.AsnStatus != AsnStatus.Cancelled && a.AsnStatus != AsnStatus.Rejected
                           && values.Contains(s.SerialNumber)
                     select new Hit(pol.ItemCode, s.SerialNumber, a.AsnNumber)).ToListAsync(ct)
            : await (from l in db.AsnLineLots
                     join al in db.AsnLines on l.AsnLineId equals al.Id
                     join a in db.Asns on al.AsnId equals a.Id
                     join pol in db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                     where !l.IsDeleted && !al.IsDeleted && !a.IsDeleted
                           && a.TenantEntityId == companyId
                           && (excludeAsnId == null || a.Id != excludeAsnId)
                           && a.AsnStatus != AsnStatus.Cancelled && a.AsnStatus != AsnStatus.Rejected
                           && values.Contains(l.LotNo)
                     select new Hit(pol.ItemCode, l.LotNo, a.AsnNumber)).ToListAsync(ct);

        if (existing.Count == 0) return;

        var label = isSerial ? "Serial number" : "Lot number";
        var wanted = captures
            .GroupBy(c => (c.ItemCode, c.Value), CaptureComparer.Instance)
            .Select(g => g.Key)
            .ToList();

        foreach (var w in wanted)
        {
            var hit = existing.FirstOrDefault(e =>
                string.Equals(e.ItemCode, w.ItemCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Value, w.Value, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                addError("lines",
                    $"{label} '{w.Value}' is already used for item '{w.ItemCode}' on ASN {hit.AsnNumber}.");
        }
    }

    private sealed record Hit(string ItemCode, string Value, string AsnNumber);

    /// <summary>Case-insensitive on both halves of the (itemCode, value) key.</summary>
    private sealed class CaptureComparer : IEqualityComparer<(string ItemCode, string Value)>
    {
        public static readonly CaptureComparer Instance = new();

        public bool Equals((string ItemCode, string Value) a, (string ItemCode, string Value) b) =>
            string.Equals(a.ItemCode, b.ItemCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ItemCode, string Value) k) =>
            HashCode.Combine(
                k.ItemCode?.ToUpperInvariant() ?? string.Empty,
                k.Value?.ToUpperInvariant() ?? string.Empty);
    }
}
