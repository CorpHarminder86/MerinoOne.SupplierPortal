using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Queries;

/// <summary>
/// 2026-10-10 — PO type-ahead for lookup / picker controls (<c>GET api/purchase-orders/lookup</c>). Returns the lightweight
/// <see cref="PoLookupItemDto"/> row (identity, supplier, date, status) — no lines, no totals — for POs whose number
/// CONTAINS the query text.
///
/// <list type="bullet">
///   <item><c>Q</c> is trimmed and must be at least <see cref="LookupPurchaseOrdersQueryHandler.MinQueryLength"/> characters;
///         anything shorter is an EMPTY list (a type-ahead must not enumerate the whole PO table on the first keystroke).</item>
///   <item>The match is case-insensitive whatever the database collation (upper-cased on both sides), and the text is
///         matched literally — <c>%</c>, <c>_</c> and <c>[</c> carry no wildcard meaning.</item>
///   <item>Newest first: <c>PoDate</c> DESC, then <c>PoNumber</c>. <c>Limit</c> defaults to 10 and is capped at 50.</item>
///   <item><c>SupplierId</c> optionally narrows to one supplier.</item>
/// </list>
///
/// <para>Scoping is the list query's, unchanged: the always-on tenant / company / seccode filters apply through
/// <see cref="IAppDbContext"/>, so a supplier user only ever sees its own POs. The supplier name is obtained the same way
/// the PO list does it — an inner join to the (equally filtered) supplier, <c>LegalName</c> — so the lookup can never offer
/// a PO the list would not show.</para>
/// </summary>
public record LookupPurchaseOrdersQuery(string? Q, Guid? SupplierId = null, int Limit = LookupPurchaseOrdersQueryHandler.DefaultLimit)
    : IRequest<IReadOnlyList<PoLookupItemDto>>;

public class LookupPurchaseOrdersQueryHandler : IRequestHandler<LookupPurchaseOrdersQuery, IReadOnlyList<PoLookupItemDto>>
{
    /// <summary>Fewest characters (after trimming) that start a search.</summary>
    public const int MinQueryLength = 2;

    /// <summary>Rows returned when the caller gives no (or a non-positive) limit.</summary>
    public const int DefaultLimit = 10;

    /// <summary>Hard cap on the rows returned, whatever limit is asked for.</summary>
    public const int MaxLimit = 50;

    private readonly IAppDbContext _db;
    public LookupPurchaseOrdersQueryHandler(IAppDbContext db) => _db = db;

    public async Task<IReadOnlyList<PoLookupItemDto>> Handle(LookupPurchaseOrdersQuery request, CancellationToken ct)
    {
        var text = request.Q?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < MinQueryLength)
            return Array.Empty<PoLookupItemDto>();

        var limit = request.Limit < 1 ? DefaultLimit : Math.Min(request.Limit, MaxLimit);
        var needle = text.ToUpperInvariant();

        var query = from po in _db.PurchaseOrders
                    join s in _db.Suppliers on po.SupplierId equals s.Id
                    where po.PoNumber.ToUpper().Contains(needle)
                    select new { po, s };

        if (request.SupplierId is Guid supplierId)
            query = query.Where(x => x.po.SupplierId == supplierId);

        // Materialise the raw columns, then name the status client-side: the enum NAME is the contract (the UI renders it
        // through PoStatusLabels), and doing it after the read keeps the SQL to plain columns.
        var rows = await query
            .OrderByDescending(x => x.po.PoDate).ThenBy(x => x.po.PoNumber)
            .Take(limit)
            .Select(x => new { x.po.Id, x.po.PoNumber, x.s.LegalName, x.po.PoDate, x.po.PoStatus })
            .ToListAsync(ct);

        return rows
            .Select(r => new PoLookupItemDto(r.Id, r.PoNumber, r.LegalName, r.PoDate, r.PoStatus.ToString()))
            .ToList();
    }
}
