using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// R5 (TSD R5 Addendum §9) — create a <b>Draft</b> ASN from selected Delivery Schedule lines. The supplier
/// multi-selects schedule lines; this builds ONE ASN grouped by <c>(supplierId, shipToAddressId)</c>:
/// <list type="bullet">
///   <item>All selected schedules MUST share ONE <c>shipToAddressId</c> (cross-ship-to blocked — UC-AS-02) and
///         ONE supplier; lines MAY span multiple POs (UC-AS-01).</item>
///   <item>Each schedule → one <see cref="AsnLine"/> referencing its <c>purchaseOrderLineId</c> +
///         <c>deliveryScheduleId</c>; ship qty defaults to the line's remaining balance (editable, §9.2).</item>
///   <item>The header sets <see cref="Asn.ShipToAddressId"/>; the deprecated <see cref="Asn.PurchaseOrderId"/> is
///         left NULL (PO linkage is at the line level). The junction is still populated from the distinct line POs
///         so the downstream submit/invoice/uniqueness logic resolves covered POs uniformly.</item>
/// </list>
/// <para><b>NO balance consumption at create (§10.4)</b> — the atomic over-ship guard runs only at final Submit.</para>
/// <para><b>Persist-time invariant (§9.3):</b> every line's <c>PurchaseOrder.shipToAddressId == Asn.shipToAddressId</c>,
/// single supplier — asserted as defence in depth behind the UI guard.</para>
/// </summary>
public record CreateAsnFromScheduleCommand(CreateAsnFromScheduleRequest Body) : IRequest<AsnDetailDto>;

public class CreateAsnFromScheduleCommandValidator : AbstractValidator<CreateAsnFromScheduleCommand>
{
    public CreateAsnFromScheduleCommandValidator()
    {
        RuleFor(x => x.Body.ExpectedDeliveryDate).NotEmpty();
        RuleFor(x => x.Body.ScheduleIds).NotNull().NotEmpty()
            .WithMessage("At least one delivery schedule must be selected.");
    }
}

public class CreateAsnFromScheduleCommandHandler : IRequestHandler<CreateAsnFromScheduleCommand, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly Common.Documents.AsnAttachmentRebinder _rebinder;
    private readonly IFulfilmentSettings _fulfilment;

    public CreateAsnFromScheduleCommandHandler(
        IAppDbContext db, ICurrentUser user, Common.Documents.AsnAttachmentRebinder rebinder, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _rebinder = rebinder; _fulfilment = fulfilment;
    }

    public async Task<AsnDetailDto> Handle(CreateAsnFromScheduleCommand request, CancellationToken ct)
    {
        var body = request.Body;
        var scheduleIds = body.ScheduleIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (scheduleIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["scheduleIds"] = new[] { "At least one delivery schedule must be selected." }
            });

        // Load the selected schedules joined to their PO line + PO (for ship-to, supplier, balance, position).
        var rows = await (from s in _db.DeliverySchedules
                          join pol in _db.PurchaseOrderLines on s.PurchaseOrderLineId equals pol.Id
                          join po in _db.PurchaseOrders on s.PurchaseOrderId equals po.Id
                          where scheduleIds.Contains(s.Id) && !s.IsDeleted
                          select new
                          {
                              ScheduleId = s.Id,
                              s.ShipToAddressId,
                              PoShipToAddressId = po.ShipToAddressId,
                              s.PurchaseOrderId,
                              s.PurchaseOrderLineId,
                              po.SupplierId,
                              po.SeccodeId,
                              po.TenantEntityId,
                              pol.ItemCode,
                              pol.ItemId,
                              pol.OrderQty,
                              pol.ShippedQtyToDate,
                              pol.PositionNo,
                              pol.SequenceNo,
                          }).ToListAsync(ct);

        var missing = scheduleIds.Except(rows.Select(r => r.ScheduleId)).ToList();
        if (missing.Count > 0)
            throw new NotFoundException("DeliverySchedule", string.Join(", ", missing));

        // ── Single supplier (UC-AS-01) ───────────────────────────────────────────────────────────────────
        var supplierIds = rows.Select(r => r.SupplierId).Distinct().ToList();
        if (supplierIds.Count != 1)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["scheduleIds"] = new[] { "All selected schedules must belong to the same supplier." }
            });
        var supplierId = supplierIds[0];

        // ── Single ship-to (UC-AS-02) — reject cross-ship-to selection with a clear message ────────────────
        var shipToIds = rows.Select(r => r.ShipToAddressId).Distinct().ToList();
        if (shipToIds.Count != 1)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["shipToAddressId"] = new[]
                {
                    "An ASN cannot mix ship-to addresses; all selected delivery schedules must share one ship-to."
                }
            });
        var shipToAddressId = shipToIds[0];

        // ── §9.3 invariant (defence in depth): every line's PO.shipToAddressId == Asn.shipToAddressId ──────
        if (rows.Any(r => r.PoShipToAddressId != shipToAddressId))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["shipToAddressId"] = new[]
                {
                    "A selected schedule's purchase order ship-to does not match the ASN ship-to (invariant §9.3)."
                }
            });

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, ct)
                       ?? throw new NotFoundException("Supplier", supplierId);

        // Item control flags (serialized XOR lot-controlled) for the per-line capture wiring. Resolve by ItemCode
        // within the company (the PO line's ItemId is routinely null). IgnoreQueryFilters — Item is company-scoped.
        var itemCompany = rows[0].TenantEntityId;
        var itemCodes = rows.Select(r => r.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var itemFlags = (await _db.Items.IgnoreQueryFilters()
                .Where(i => i.TenantEntityId == itemCompany && !i.IsDeleted && itemCodes.Contains(i.Code))
                .Select(i => new { i.Code, i.Id, i.IsSerialized, i.IsLotControlled })
                .ToListAsync(ct))
            .ToDictionary(i => i.Code, i => i, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var asnId = Guid.NewGuid();
        var asnNumber = $"ASN-{supplier.SupplierCode}-{now:yyyyMMddHHmmssfff}";
        var shipQtyOverrides = body.ShipQtyByScheduleId ?? new Dictionary<Guid, decimal>();

        var asn = new Asn
        {
            Id = asnId,
            AsnNumber = asnNumber,
            PurchaseOrderId = null,                 // R5 §9.2 — header PO deprecated; PO linkage is per line.
            ShipToAddressId = shipToAddressId,       // R5 §9.2 — the grouping key.
            SupplierId = supplierId,
            ExpectedDeliveryDate = body.ExpectedDeliveryDate,
            TimeWindow = body.TimeWindow,
            CarrierName = body.CarrierName,
            TrackingNumber = body.TrackingNumber,
            VehicleNumber = body.VehicleNumber,
            DriverName = body.DriverName,
            DriverPhone = body.DriverPhone,
            Notes = body.Notes,
            AsnStatus = AsnStatus.Draft,
            SeccodeId = rows[0].SeccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = now,
        };

        // Junction rows for every covered PO (the distinct line POs) — keeps the downstream submit/invoice/
        // uniqueness covered-PO resolution uniform even though the header PO is null.
        foreach (var poId in rows.Select(r => r.PurchaseOrderId).Distinct())
        {
            asn.PurchaseOrders.Add(new AsnPurchaseOrder
            {
                Id = Guid.NewGuid(),
                AsnId = asnId,
                PurchaseOrderId = poId,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            });
        }

        foreach (var r in rows)
        {
            var flags = !string.IsNullOrWhiteSpace(r.ItemCode) && itemFlags.TryGetValue(r.ItemCode, out var f) ? f : null;

            // Default ship qty = the line's remaining balance (editable via the per-schedule override, §9.2).
            var balance = Math.Max(0m, r.OrderQty - r.ShippedQtyToDate);
            var shipQty = shipQtyOverrides.TryGetValue(r.ScheduleId, out var ov) ? ov : balance;

            asn.Lines.Add(new AsnLine
            {
                Id = Guid.NewGuid(),
                AsnId = asnId,
                PurchaseOrderLineId = r.PurchaseOrderLineId,
                DeliveryScheduleId = r.ScheduleId,    // R5 §4.5 / §9.2 — back-link to the originating schedule.
                ItemId = r.ItemId ?? flags?.Id,
                ShippedQty = shipQty,
                PositionNo = r.PositionNo,            // snapshot from the source PO line (Addendum A4).
                SequenceNo = r.SequenceNo,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            });
            // Serial/lot capture wiring is preserved: a from-schedule create defaults qty only; the supplier
            // captures serials/lots in a follow-up Update before sending for approval (Submit re-validates).
        }

        _db.Asns.Add(asn);

        // Rebind any files uploaded DURING creation onto this ASN, in the SAME transaction.
        await _rebinder.RebindAsync(body.StagingKey, null, asnId, asn.SeccodeId, now, ct);

        // §9 — the PO confirmation gate is satisfied BY CONSTRUCTION (a schedule only exists once the PO is
        // confirmed/shippable, §8.1), so no gate re-check is needed at this create. NO balance consumption (§10.4).
        await _db.SaveChangesAsync(ct);

        return await AsnDtoBuilder.BuildAsync(_db, asnId, ct, _fulfilment.OverShipAllowanceRounding);
    }
}
