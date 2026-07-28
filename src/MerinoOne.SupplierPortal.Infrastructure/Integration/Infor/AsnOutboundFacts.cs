using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// R11 — the cross-entity facts the ASN outbound payload needs beyond the ASN aggregate itself, loaded once and
/// shared by BOTH builders (<see cref="AsnOutboundPayloadBuilder"/> and the R9 input-document builder) so the
/// two cannot drift. The byte-parity test asserts they don't.
/// <para>Every query uses <c>IgnoreQueryFilters</c> with <c>!IsDeleted</c> re-applied by hand: this runs in the
/// background OutboxDispatcher scope, which has no ambient tenant/seccode, so the global filters would otherwise
/// return nothing and silently null out half the payload.</para>
/// </summary>
internal sealed class AsnOutboundFacts
{
    /// <summary>Per-line facts resolved from the source PO line and its header.</summary>
    internal sealed record PoLineFacts(string? ItemCode, string? OrderUnit, string? PoNumber, string? PoOrigin);

    private readonly IReadOnlyDictionary<Guid, PoLineFacts> _byPoLineId;

    /// <summary>ERP company code — the same value LN sends inbound as <c>companyCode</c>.</summary>
    public string? CompanyCode { get; }

    /// <summary>LN business-partner id for the shipping supplier (<c>Supplier.ErpCode</c>).</summary>
    public string? SupplierBp { get; }

    private AsnOutboundFacts(string? companyCode, string? supplierBp, IReadOnlyDictionary<Guid, PoLineFacts> byPoLineId)
    {
        CompanyCode = companyCode;
        SupplierBp = supplierBp;
        _byPoLineId = byPoLineId;
    }

    public PoLineFacts? PoLine(Guid purchaseOrderLineId) =>
        _byPoLineId.TryGetValue(purchaseOrderLineId, out var f) ? f : null;

    /// <summary>
    /// Formats a <see cref="DateOnly"/> capture expiry as an ISO-8601 round-trip timestamp, matching every other
    /// date on the wire. R11 deliberately normalises this: the pre-R11 payload emitted line expiry as "o" but lot
    /// expiry as "yyyy-MM-dd", so LN received two date formats in one document.
    /// </summary>
    public static string? FormatDate(DateOnly? d) =>
        d?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("o");

    public static async Task<AsnOutboundFacts> LoadAsync(IAppDbContext db, Asn asn, CancellationToken ct = default)
    {
        var poLineIds = asn.Lines.Where(l => !l.IsDeleted).Select(l => l.PurchaseOrderLineId).Distinct().ToList();

        var byPoLineId = (await (from pol in db.PurchaseOrderLines.IgnoreQueryFilters()
                                 join po in db.PurchaseOrders.IgnoreQueryFilters()
                                     on pol.PurchaseOrderId equals po.Id
                                 where poLineIds.Contains(pol.Id) && !pol.IsDeleted && !po.IsDeleted
                                 select new
                                 {
                                     pol.Id,
                                     pol.ItemCode,
                                     pol.OrderUnit,
                                     po.PoNumber,
                                     po.PoOrigin,
                                 }).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => new PoLineFacts(x.ItemCode, x.OrderUnit, x.PoNumber, x.PoOrigin));

        var companyCode = asn.TenantEntityId is { } companyId
            ? await db.TenantEntities.IgnoreQueryFilters()
                .Where(t => t.Id == companyId && !t.IsDeleted)
                .Select(t => t.Code).FirstOrDefaultAsync(ct)
            : null;

        var supplierBp = await db.Suppliers.IgnoreQueryFilters()
            .Where(s => s.Id == asn.SupplierId && !s.IsDeleted)
            .Select(s => s.ErpCode).FirstOrDefaultAsync(ct);

        return new AsnOutboundFacts(companyCode, supplierBp, byPoLineId);
    }
}
