using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Common;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

/// <summary>
/// R4 (2026-06-22) — Module 3. Paged ASN list. Multi-PO-aware (Q1): the header PO may be null, so PO display is
/// derived from the AsnPurchaseOrder junction (PoSummary = comma-joined PO numbers, PoCount = covered-PO count).
/// The <c>?? Guid.Empty</c> shim is removed; <c>PurchaseOrderId</c> filter matches the header PO OR any junction PO.
///
/// <para>R18 (2026-11-03) — <c>Search</c> also matches the number of any PO the ASN covers (header PO or junction PO), and
/// <c>ExpectedFrom</c>/<c>ExpectedTo</c> bound the Expected-delivery column by calendar day, both ends inclusive.</para>
/// </summary>
public record GetAsnListQuery(
    int Page = 1,
    int PageSize = 50,
    string? Status = null,
    Guid? SupplierId = null,
    Guid? PurchaseOrderId = null,
    string? Search = null,
    DateOnly? ExpectedFrom = null,
    DateOnly? ExpectedTo = null) : IRequest<PagedResult<AsnListItemDto>>;

public class GetAsnListQueryValidator : AbstractValidator<GetAsnListQuery>
{
    public GetAsnListQueryValidator()
    {
        // R18 (2026-11-03) — a range that ends before it starts can never match a row; a 400 beats a silently empty list.
        RuleFor(x => x).Must(x => !x.ExpectedFrom.HasValue || !x.ExpectedTo.HasValue || x.ExpectedFrom <= x.ExpectedTo)
            .WithMessage("Expected to must be on or after Expected from.");
    }
}

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
            // R18 (2026-11-03) — also the number of any PO the ASN covers: the header PO, or a live junction PO (a multi-PO ASN
            // carries only those). Existence probes rather than a join, so an ASN covering several matching POs is still one row.
            q = q.Where(x => x.a.AsnNumber.Contains(t) || x.s.LegalName.Contains(t)
                             || _db.PurchaseOrders.Any(po => po.Id == x.a.PurchaseOrderId && po.PoNumber.Contains(t))
                             || _db.AsnPurchaseOrders.Any(j => j.AsnId == x.a.Id && !j.IsDeleted
                                    && _db.PurchaseOrders.Any(po => po.Id == j.PurchaseOrderId && po.PoNumber.Contains(t))));
        }
        // R18 (2026-11-03) — Expected-delivery day range, both ends inclusive. The same half-open window as the delivery-schedule
        // grid (>= from 00:00, < to + 1 day 00:00), except the day edges are display-zone midnights run through AppTime: the
        // Expected column renders AppTime.Date(utc), so a row showing the 'to' day must be IN and the next day's row OUT.
        if (request.ExpectedFrom is { } fromDay)
        {
            var fromUtc = StartOfDayUtc(fromDay);
            q = q.Where(x => x.a.ExpectedDeliveryDate >= fromUtc);
        }
        // 9999-12-31 is the last day the calendar has: it has no next midnight to cut at (DateOnly.AddDays would throw) and nothing
        // the store can hold lies after it, so as a 'to' it is open-ended. The first day needs no such care — AppTime.ToUtc clamps.
        if (request.ExpectedTo is { } toDay && toDay < DateOnly.MaxValue)
        {
            var beforeUtc = StartOfDayUtc(toDay.AddDays(1));
            q = q.Where(x => x.a.ExpectedDeliveryDate < beforeUtc);
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
                x.a.CarrierCode, x.a.ErpShipmentStatus,   // R17
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
                x.Status, x.SubmittedAt, x.CreatedOn,
                CarrierCode: x.CarrierCode, ErpShipmentStatus: x.ErpShipmentStatus);   // R17 (Warehouse stays at its default)
        }).ToList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);
        return new PagedResult<AsnListItemDto>(items, page, pageSize, total, totalPages);
    }

    /// <summary>The UTC instant at which <paramref name="day"/> begins on the display clock (<see cref="AppTime.Zone"/>).</summary>
    private static DateTime StartOfDayUtc(DateOnly day) => AppTime.ToUtc(day.ToDateTime(TimeOnly.MinValue));
}
