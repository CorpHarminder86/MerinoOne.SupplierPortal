using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

/// <summary>
/// R4 (2026-06-22) — Module 3. Paged ASN list. Multi-PO-aware (Q1): the header PO may be null, so PO display is
/// derived from the AsnPurchaseOrder junction (PoSummary = comma-joined PO numbers, PoCount = covered-PO count).
/// The <c>?? Guid.Empty</c> shim is removed; <c>PurchaseOrderId</c> filter matches the header PO OR any junction PO.
/// </summary>
public record GetAsnListQuery(
    int Page = 1,
    int PageSize = 50,
    string? Status = null,
    Guid? SupplierId = null,
    Guid? PurchaseOrderId = null,
    string? Search = null) : IRequest<PagedResult<AsnListItemDto>>;

public class GetAsnListQueryHandler : IRequestHandler<GetAsnListQuery, PagedResult<AsnListItemDto>>
{
    private readonly IAppDbContext _db;
    public GetAsnListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<PagedResult<AsnListItemDto>> Handle(GetAsnListQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize is < 1 or > 200 ? 50 : request.PageSize;

        var q = from a in _db.Asns
                join s in _db.Suppliers on a.SupplierId equals s.Id
                select new { a, s };

        if (!string.IsNullOrWhiteSpace(request.Status))
            q = q.Where(x => x.a.AsnStatus.ToString() == request.Status);
        if (request.SupplierId.HasValue)
            q = q.Where(x => x.a.SupplierId == request.SupplierId.Value);
        if (request.PurchaseOrderId.HasValue)
        {
            var poId = request.PurchaseOrderId.Value;
            q = q.Where(x => x.a.PurchaseOrderId == poId
                             || _db.AsnPurchaseOrders.Any(j => j.AsnId == x.a.Id && j.PurchaseOrderId == poId && !j.IsDeleted));
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var t = request.Search.Trim();
            q = q.Where(x => x.a.AsnNumber.Contains(t) || x.s.LegalName.Contains(t));
        }

        var total = await q.CountAsync(ct);

        var page1 = await q
            .OrderByDescending(x => x.a.ExpectedDeliveryDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.a.Id, x.a.Seq, x.a.AsnNumber, x.a.PurchaseOrderId,
                x.a.SupplierId, SupplierName = x.s.LegalName,
                x.a.ExpectedDeliveryDate, x.a.CarrierName, x.a.TrackingNumber,
                Status = x.a.AsnStatus.ToString(), x.a.SubmittedAt, x.a.CreatedOn,
            })
            .ToListAsync(ct);

        // Resolve covered-PO numbers per ASN (junction ∪ header) in one round-trip.
        var asnIds = page1.Select(x => x.Id).ToList();
        var junctionPos = await (from j in _db.AsnPurchaseOrders
                                 join po in _db.PurchaseOrders on j.PurchaseOrderId equals po.Id
                                 where asnIds.Contains(j.AsnId) && !j.IsDeleted
                                 select new { j.AsnId, po.PoNumber, po.Id })
                                .ToListAsync(ct);
        var headerPoIds = page1.Where(x => x.PurchaseOrderId.HasValue).Select(x => x.PurchaseOrderId!.Value).Distinct().ToList();
        var headerPos = await _db.PurchaseOrders.Where(p => headerPoIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PoNumber }).ToDictionaryAsync(p => p.Id, ct);

        var poNumbersByAsn = junctionPos
            .GroupBy(x => x.AsnId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PoNumber).Distinct().OrderBy(n => n).ToList());

        var items = page1.Select(x =>
        {
            var nums = poNumbersByAsn.TryGetValue(x.Id, out var list) ? new List<string>(list) : new List<string>();
            string? headerPoNumber = x.PurchaseOrderId.HasValue && headerPos.TryGetValue(x.PurchaseOrderId.Value, out var hp) ? hp.PoNumber : null;
            if (headerPoNumber is not null && !nums.Contains(headerPoNumber)) nums.Insert(0, headerPoNumber);
            var summary = nums.Count == 0 ? "—" : string.Join(", ", nums);
            return new AsnListItemDto(
                x.Id, x.Seq, x.AsnNumber, x.PurchaseOrderId, headerPoNumber,
                summary, nums.Count,
                x.SupplierId, x.SupplierName,
                x.ExpectedDeliveryDate, x.CarrierName, x.TrackingNumber,
                x.Status, x.SubmittedAt, x.CreatedOn);
        }).ToList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<AsnListItemDto>(items, page, pageSize, total, totalPages);
    }
}
