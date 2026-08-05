using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Shipments.Queries;

/// <summary>
/// R5/R13 (TSD R5 Addendum §7 / §8.3) — delivery-schedule grid. Returns schedule rows joined to their PO line so the
/// grid shows PO number, position, item, order qty + warehouse, with RemainingToShip DERIVED from the R4 line balance
/// (orderQty − shippedQtyToDate) — all in ONE projection (no entity loads). Sort: PO → Line → DeliveryDate ASC.
///
/// <para>Filters (§7): supplier, warehouse, PO, delivery-date range, status. The result also carries the auto-hide
/// warehouse signal — the count of DISTINCT warehouse addresses across the supplier's visible schedules (before the
/// warehouse filter) so the UI hides the Warehouse filter when there is only one to pick.</para>
///
/// <para>RLS: the query runs under the caller's principal — the always-on seccode/company filters scope a supplier
/// to its own schedules (each schedule carries the owning PO's G-seccode), so no explicit supplier guard is needed
/// beyond the optional supplier filter.</para>
/// </summary>
public record GetDeliveryScheduleListQuery(DeliveryScheduleFilterRequest Filter)
    : IRequest<DeliveryScheduleGridDto>;

public class GetDeliveryScheduleListQueryHandler
    : IRequestHandler<GetDeliveryScheduleListQuery, DeliveryScheduleGridDto>
{
    private readonly IAppDbContext _db;
    public GetDeliveryScheduleListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<DeliveryScheduleGridDto> Handle(GetDeliveryScheduleListQuery request, CancellationToken ct)
    {
        var f = request.Filter;
        var page = f.Page < 1 ? 1 : f.Page;
        var pageSize = f.PageSize is < 1 or > 500 ? 50 : f.PageSize;

        // Base join: schedule → its PO line (for position / item / order qty / shipped) → its PO (number / supplier)
        // → the WAREHOUSE address (name) → the supplier (name). Projection only; no entity loads. The schedule's own
        // RLS filters apply (it is BaseAggregateRoot, seccode-owned), so a supplier sees only its rows.
        var baseQuery =
            from sch in _db.DeliverySchedules.Where(s => !s.IsDeleted)
            join line in _db.PurchaseOrderLines on sch.PurchaseOrderLineId equals line.Id
            join po in _db.PurchaseOrders on sch.PurchaseOrderId equals po.Id
            join addr in _db.CompanyAddresses.IgnoreQueryFilters() on sch.WarehouseAddressId equals (Guid?)addr.Id into addrJoin
            from addr in addrJoin.DefaultIfEmpty()
            join sup in _db.Suppliers.IgnoreQueryFilters() on po.SupplierId equals sup.Id into supJoin
            from sup in supJoin.DefaultIfEmpty()
            select new
            {
                sch,
                line,
                PoNumber = po.PoNumber,
                // R13 (D4) — the receiving warehouse is now the PO LINE's code (was the retired PO-header warehouse).
                LineWarehouse = line.Warehouse,
                SupplierId = po.SupplierId,
                SupplierName = sup != null ? sup.LegalName : null,
                WarehouseAddressName = addr != null ? addr.AddressName : null,
                PoStatus = po.PoStatus,
                PoConfirmationMode = sup != null ? sup.PoConfirmationMode : PoConfirmationMode.AcceptToShip,
            };

        // Filters (§7). Supplier / PO / ship-to / status / delivery-date range — all optional.
        if (f.SupplierId is Guid sid) baseQuery = baseQuery.Where(x => x.SupplierId == sid);
        if (f.PurchaseOrderId is Guid pid) baseQuery = baseQuery.Where(x => x.sch.PurchaseOrderId == pid);
        if (!string.IsNullOrWhiteSpace(f.Status)
            && Enum.TryParse<DeliveryScheduleStatus>(f.Status, true, out var st))
        {
            baseQuery = baseQuery.Where(x => x.sch.Status == st);
        }
        if (f.DeliveryDateFrom is DateTime from) baseQuery = baseQuery.Where(x => x.sch.DeliveryDate >= from.Date);
        if (f.DeliveryDateTo is DateTime to) baseQuery = baseQuery.Where(x => x.sch.DeliveryDate < to.Date.AddDays(1));

        // §7 — auto-hide warehouse signal: the count of DISTINCT warehouse addresses across the rows the supplier can
        // see BEFORE the warehouse filter is applied. Computed off the filtered-but-not-warehouse-filtered set so
        // adding the filter never changes the "how many warehouses exist" answer. The UI hides the filter when ≤ 1.
        var distinctWarehouseCount = await baseQuery
            .Select(x => x.sch.WarehouseAddressId).Distinct().CountAsync(ct);

        // Apply the warehouse filter AFTER the distinct count (so the count reflects all available warehouses).
        if (f.WarehouseAddressId is Guid warehouseAddr) baseQuery = baseQuery.Where(x => x.sch.WarehouseAddressId == warehouseAddr);

        var total = await baseQuery.CountAsync(ct);

        // Sort PO → Line → DeliveryDate ASC (§8.3). PO by number, line by position, then the schedule date. Project
        // to a flat intermediate (carrying PoStatus + supplier mode) so IsShippable can be computed in-memory below
        // (PoConfirmationPolicy is not translatable to SQL and the Web layer cannot call it).
        var raw = await baseQuery
            .OrderBy(x => x.PoNumber).ThenBy(x => x.line.PositionNo).ThenBy(x => x.sch.DeliveryDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.sch.Id,
                x.sch.Seq,
                x.sch.PurchaseOrderId,
                x.PoNumber,
                x.sch.PurchaseOrderLineId,
                x.line.PositionNo,
                x.line.ItemCode,
                x.line.ItemDescription,
                x.line.OrderUnit,
                x.line.OrderQty,
                x.line.ShippedQtyToDate,
                x.sch.WarehouseAddressId,
                x.WarehouseAddressName,
                // R13 (D4) — the PO LINE's receiving warehouse code.
                x.LineWarehouse,
                x.SupplierId,
                x.SupplierName,
                x.sch.ScheduledQty,
                x.sch.DeliveryDate,
                Status = x.sch.Status,
                x.sch.CreatedOn,
                x.PoStatus,
                x.PoConfirmationMode,
            })
            .ToListAsync(ct);

        var rows = raw.Select(r => new DeliveryScheduleDto(
            r.Id, r.Seq, r.PurchaseOrderId, r.PoNumber, r.PurchaseOrderLineId, r.PositionNo,
            r.ItemCode, r.ItemDescription, r.OrderUnit, r.OrderQty, r.ShippedQtyToDate,
            // RemainingToShip DERIVED from the R4 line balance — MAX(0, orderQty − shippedQtyToDate).
            r.OrderQty - r.ShippedQtyToDate > 0 ? r.OrderQty - r.ShippedQtyToDate : 0m,
            r.SupplierId, r.SupplierName, r.ScheduledQty,
            r.DeliveryDate, r.Status.ToString(), r.CreatedOn,
            PoConfirmationPolicy.AllowsShipping(r.PoStatus, r.PoConfirmationMode),
            // R13 (D4) — warehouse: the PO LINE's code + the warehouse address FK/name (was ship-to).
            r.LineWarehouse, r.WarehouseAddressId, r.WarehouseAddressName)).ToList();

        var totalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        var paged = new PagedResult<DeliveryScheduleDto>(rows, page, pageSize, total, totalPages);

        return new DeliveryScheduleGridDto(paged, distinctWarehouseCount, ShowWarehouseFilter: distinctWarehouseCount > 1);
    }
}
