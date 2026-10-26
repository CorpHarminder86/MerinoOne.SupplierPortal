using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments;

/// <summary>
/// R17 (2026-10-26) — resolves the carrier a supplier picked on an ASN into the pair the ASN stores: the master
/// <c>Carrier.Code</c> and a frozen copy of its description (snapshot-on-write — reads show the stored description and
/// never join the master, so a later LN re-sync of the carrier cannot rewrite a shipped ASN).
///
/// <para><b>Non-blank code</b> → the active, non-deleted <c>proc.Carrier</c> row of the caller's company (the normal
/// tenant + share-aware company query filter applies — NOT <c>IgnoreQueryFilters</c>) matched case-insensitively on the
/// trimmed code. Found → <c>(carrier.Code, carrier.Description ?? carrier.Code)</c> (LN may send a code with no name, so
/// the code stands in as its own label). Unknown or inactive → a field-keyed <see cref="ValidationException"/>
/// (<c>carrierCode</c>), HTTP 400.</para>
///
/// <para><b>Blank code</b> → <c>(null, legacyName)</c>: the pre-R17 free-text <c>CarrierName</c> passes straight through
/// unchanged, so drafts and callers that never adopted the picker behave exactly as before. Post is where a carrier
/// becomes mandatory (<see cref="Commands.PostAsnCommandHandler"/>), not here.</para>
/// </summary>
public static class CarrierSnapshot
{
    public static async Task<(string? Code, string? Name)> ResolveAsync(
        IAppDbContext db, string? carrierCode, string? legacyName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(carrierCode)) return (null, legacyName);

        var code = carrierCode.Trim();
        var upper = code.ToUpperInvariant();

        // The column collation is not relied on for the case-insensitive match: upper-casing both sides makes the intent
        // explicit and independent of it (the table is a few dozen rows per company, so the lost index seek is moot).
        var carrier = await db.Carriers
            .Where(c => c.Code.ToUpper() == upper && c.IsActive && !c.IsDeleted)
            .Select(c => new { c.Code, c.Description })
            .FirstOrDefaultAsync(ct);

        if (carrier is null)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["carrierCode"] = new[] { $"Unknown or inactive carrier '{code}'." }
            });

        return (carrier.Code, carrier.Description ?? carrier.Code);
    }
}
