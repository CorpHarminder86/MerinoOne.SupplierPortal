using System.Globalization;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>
/// R12 — the pieces both PO_Update input documents need: the LN company code, the wire date format, the
/// PO line set, and the D7 "do all the lines agree on one date?" test.
///
/// <para>Shared deliberately: <c>PoAccept</c>/<c>PoReject</c> build from the PO and
/// <c>PoNegotiationApprove</c> builds from the negotiation, but both must produce the SAME
/// <c>lines</c> / <c>headerDeliveryDate</c> shape or the three request expressions stop being
/// interchangeable text.</para>
/// </summary>
internal static class PoLineDocumentAssembler
{
    /// <summary>
    /// Wire date format (R12/D4, verified against LN 2026-07-29 probe run 2): ISO-8601 seconds precision with
    /// an explicit <c>Z</c>, which LN echoes back unchanged.
    ///
    /// <para><b>The <see cref="DateTime.SpecifyKind"/> call is load-bearing, not defensive.</b> SQL Server
    /// hands back <c>DateTimeKind.Unspecified</c>; formatting that without a zone emits a naive string, and a
    /// naive string is exactly what LN defaults to <b>UTC−05:00</b> — probe run 1 sent <c>11:00:00</c> and got
    /// <c>16:00:00Z</c> back. The portal stores UTC, so stamping the kind is what makes the value honest.
    /// <c>ToUniversalTime()</c> would be WRONG here: on an <c>Unspecified</c> value it applies the server's
    /// local offset.</para>
    ///
    /// <para>Not the repo's usual <c>"o"</c>: that emits seven fractional digits and every LN sample has none.</para>
    /// </summary>
    public static string? FormatUtc(DateTime? value) => value is not { } dt
        ? null
        : DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// D7 — the single delivery date every line agrees on, or null.
    ///
    /// <para>Null when the lines disagree, when ANY line has no date, or when there are no lines. Deliberately
    /// fail-safe: a header date that is untrue of some line is worse than no header date, because LN applies
    /// it to the whole order.</para>
    ///
    /// <para>Callers pass the already-formatted strings so the comparison is ordinal over the exact bytes that
    /// would go on the wire — no risk of two <see cref="DateTime"/> values comparing equal but formatting apart.</para>
    /// </summary>
    public static string? HeaderDeliveryDate(IReadOnlyList<PurchaseOrderLineInputDoc> lines)
    {
        if (lines.Count == 0) return null;
        var first = lines[0].DeliveryDate;
        if (first is null) return null;
        foreach (var line in lines)
            if (!string.Equals(line.DeliveryDate, first, StringComparison.Ordinal))
                return null;
        return first;
    }

    /// <summary>
    /// LN logistic company for a PO (D9). Null when the PO carries no company — the builders leave it null and
    /// the dispatch fails permanently rather than guessing: <c>PO_Update</c> routes on the BODY company (D21),
    /// so unlike the OData endpoints there is no <c>X-Infor-LnCompany</c> fallback that could rescue it.
    /// </summary>
    public static async Task<string?> CompanyCodeAsync(IAppDbContext db, Guid? tenantEntityId, CancellationToken ct)
    {
        if (tenantEntityId is not { } cid) return null;
        return await db.TenantEntities.IgnoreQueryFilters()
            .Where(t => t.Id == cid && !t.IsDeleted)
            .Select(t => t.Code)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The PO's line set, ordered position then sequence.
    ///
    /// <para>D24 — EVERY non-deleted line is returned, including lines with no delivery date. The D6 filter
    /// ("only dated lines reach the wire") is applied by the JSONata, not here, because LN's behaviour for an
    /// omitted line is still unverified (probe P5a): if omitting a line turns out to close it, the fix is an
    /// expression edit instead of a rebuild.</para>
    ///
    /// <para><paramref name="dateOverrides"/> carries the D10 negotiated dates, keyed
    /// (positionNo, sequenceNo). A line absent from the map keeps its own delivery date.</para>
    /// </summary>
    public static async Task<List<PurchaseOrderLineInputDoc>> LinesAsync(
        IAppDbContext db,
        Guid purchaseOrderId,
        IReadOnlyDictionary<(int PositionNo, int SequenceNo), DateTime?>? dateOverrides,
        CancellationToken ct)
    {
        var rows = await db.PurchaseOrderLines.IgnoreQueryFilters()
            .Where(l => l.PurchaseOrderId == purchaseOrderId && !l.IsDeleted)
            .OrderBy(l => l.PositionNo).ThenBy(l => l.SequenceNo)
            .Select(l => new { l.PositionNo, l.SequenceNo, l.DeliveryDate })
            .ToListAsync(ct);

        return rows.Select(l =>
        {
            var effective = l.DeliveryDate;
            // A negotiated line with an explicit NULL date is not an override — the supplier negotiated qty or
            // price only, so the PO line's own date stands.
            if (dateOverrides is not null
                && dateOverrides.TryGetValue((l.PositionNo, l.SequenceNo), out var negotiated)
                && negotiated is not null)
                effective = negotiated;

            return new PurchaseOrderLineInputDoc(l.PositionNo, l.SequenceNo, FormatUtc(effective));
        }).ToList();
    }
}
