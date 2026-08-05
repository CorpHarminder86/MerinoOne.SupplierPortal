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
/// R4 (2026-06-22) — Module 3. Creates a <b>Draft</b> ASN. NO ERP post on create (the Increment-0 create-time
/// outbox enqueue is removed — posting happens only on <see cref="SubmitAsnCommand"/>). Supports MULTIPLE POs
/// (Q1): the AsnPurchaseOrder junction is populated from the distinct POs the chosen lines belong to; the legacy
/// scalar PurchaseOrderId is set only for a single-PO ASN (null for multi-PO). Each ASN line snapshots its source
/// PO line's PositionNo/SequenceNo (Addendum A4). Optional deferred-upload attachments are rebound on save.
/// </summary>
public record CreateAsnCommand(CreateAsnRequest Body) : IRequest<AsnDetailDto>;

public class CreateAsnCommandValidator : AbstractValidator<CreateAsnCommand>
{
    public CreateAsnCommandValidator()
    {
        RuleFor(x => x.Body.ExpectedDeliveryDate).NotEmpty();
        RuleFor(x => x.Body)
            .Must(b => (b.PurchaseOrderId.HasValue && b.PurchaseOrderId.Value != Guid.Empty)
                       || (b.PurchaseOrderIds is { Count: > 0 }))
            .WithMessage("At least one PurchaseOrderId is required (PurchaseOrderId or PurchaseOrderIds).")
            .WithName("purchaseOrderId");
        // R11 (D16) — header length caps. The three new refs are OPTIONAL here by design (D7): a Draft may be
        // parked with them blank; SendForApproval is where they become mandatory.
        RuleFor(x => x.Body.TimeWindow).MaximumLength(AsnHeaderRules.TimeWindow);
        RuleFor(x => x.Body.CarrierName).MaximumLength(AsnHeaderRules.CarrierName);
        RuleFor(x => x.Body.TrackingNumber).MaximumLength(AsnHeaderRules.TrackingNumber);
        RuleFor(x => x.Body.VehicleNumber).MaximumLength(AsnHeaderRules.VehicleNumber);
        RuleFor(x => x.Body.DriverName).MaximumLength(AsnHeaderRules.DriverName);
        RuleFor(x => x.Body.DriverPhone).MaximumLength(AsnHeaderRules.DriverPhone);
        RuleFor(x => x.Body.Notes).MaximumLength(AsnHeaderRules.Notes);
        RuleFor(x => x.Body.InvoiceNo).MaximumLength(AsnHeaderRules.InvoiceNo).WithName("invoiceNo");
        RuleFor(x => x.Body.BillOfLading).MaximumLength(AsnHeaderRules.BillOfLading).WithName("billOfLading");
        RuleFor(x => x.Body.PackingList).MaximumLength(AsnHeaderRules.PackingList).WithName("packingList");

        RuleFor(x => x.Body.Lines).NotNull().NotEmpty()
            .WithMessage("At least one ASN line is required.");
        RuleForEach(x => x.Body.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PurchaseOrderLineId).NotEmpty();
            line.RuleFor(l => l.ShippedQty).GreaterThan(0).WithMessage("ShippedQty must be greater than 0.");
            // R4 (2026-06-23) — reject duplicate serials / lot numbers WITHIN a line at the input layer so a dup
            // doesn't reach the DB unique index as a 500 (full PO-scope uniqueness + count rules run on Submit).
            line.RuleFor(l => l.Serials).Must(AsnLineRules.SerialsDistinct).WithMessage("Serial numbers must be unique within a line.");
            line.RuleFor(l => l.Lots).Must(AsnLineRules.LotNosDistinct).WithMessage("Lot numbers must be unique within a line.");
        });
    }
}

/// <summary>
/// R11 (D16) — ASN header field length caps, mirroring the column widths in <c>AsnConfiguration</c>.
/// <para>Before R11 the ASN validators carried NO length rules at all, so an over-long carrierName reached SQL
/// and surfaced as a 500 rather than a 400. The three new R11 refs are capped here alongside the pre-existing
/// fields so the whole header behaves consistently, rather than cementing "BOL over 50 gives a clean 400 but
/// carrierName over 200 gives a 500".</para>
/// </summary>
internal static class AsnHeaderRules
{
    public const int TimeWindow = 50;
    public const int CarrierName = 200;
    public const int TrackingNumber = 100;
    public const int VehicleNumber = 50;
    public const int DriverName = 100;
    public const int DriverPhone = 20;
    public const int Notes = 2000;

    // R11 (D6) — "open text textbox with max length 50".
    public const int InvoiceNo = 50;
    public const int BillOfLading = 50;
    public const int PackingList = 50;

    /// <summary>
    /// Trims and collapses blank/whitespace to null. Used on the three R11 shipment refs so a whitespace-only
    /// value cannot satisfy the Send-For-Approval mandatory gate, and so "cleared" and "never set" persist alike.
    /// </summary>
    public static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Shared input-level rules for ASN line serial/lot capture (used by Create + Update validators).</summary>
internal static class AsnLineRules
{
    public static bool SerialsDistinct(List<AsnLineSerialInput>? serials)
    {
        if (serials is null) return true;
        var nonEmpty = serials.Where(s => !string.IsNullOrWhiteSpace(s.SerialNumber)).Select(s => s.SerialNumber.Trim()).ToList();
        return nonEmpty.Count == nonEmpty.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    public static bool LotNosDistinct(List<AsnLineLotInput>? lots)
    {
        if (lots is null) return true;
        var nonEmpty = lots.Where(l => !string.IsNullOrWhiteSpace(l.LotNo)).Select(l => l.LotNo.Trim()).ToList();
        return nonEmpty.Count == nonEmpty.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }
}

public class CreateAsnCommandHandler : IRequestHandler<CreateAsnCommand, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly Common.Documents.AsnAttachmentRebinder _rebinder;
    private readonly IFulfilmentSettings _fulfilment;

    public CreateAsnCommandHandler(
        IAppDbContext db, ICurrentUser user, Common.Documents.AsnAttachmentRebinder rebinder, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _rebinder = rebinder; _fulfilment = fulfilment;
    }

    public async Task<AsnDetailDto> Handle(CreateAsnCommand request, CancellationToken ct)
    {
        var body = request.Body;

        // Resolve the requested PO set (legacy scalar OR explicit list).
        var requestedPoIds = new HashSet<Guid>();
        if (body.PurchaseOrderId is { } pid && pid != Guid.Empty) requestedPoIds.Add(pid);
        if (body.PurchaseOrderIds is { Count: > 0 })
            foreach (var id in body.PurchaseOrderIds) if (id != Guid.Empty) requestedPoIds.Add(id);

        var pos = await _db.PurchaseOrders.Where(p => requestedPoIds.Contains(p.Id)).ToListAsync(ct);
        var missingPos = requestedPoIds.Except(pos.Select(p => p.Id)).ToList();
        if (missingPos.Count > 0)
            throw new NotFoundException("PurchaseOrder", string.Join(", ", missingPos));

        // All POs must belong to ONE supplier (an ASN ships from a single supplier).
        var supplierIds = pos.Select(p => p.SupplierId).Distinct().ToList();
        if (supplierIds.Count != 1)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["purchaseOrderIds"] = new[] { "All POs on one ASN must belong to the same supplier." }
            });
        var supplierId = supplierIds[0];
        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, ct)
                       ?? throw new NotFoundException("Supplier", supplierId);

        // Load the chosen PO lines, validate each belongs to a PO in the set, and snapshot position/sequence.
        var requestedLineIds = body.Lines.Select(l => l.PurchaseOrderLineId).Distinct().ToList();
        var poLines = await _db.PurchaseOrderLines
            .Where(l => requestedLineIds.Contains(l.Id) && requestedPoIds.Contains(l.PurchaseOrderId))
            .ToDictionaryAsync(l => l.Id, ct);

        var invalid = requestedLineIds.Except(poLines.Keys).ToList();
        if (invalid.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { $"PurchaseOrderLineId(s) not on the supplied PO(s): {string.Join(", ", invalid)}" }
            });

        // R4 (2026-06-23) — Serial/Lot capture: the Item control flags (serialized XOR lot-controlled) decide
        // which child rows to persist per line. Resolve by **ItemCode within the PO's company** (NOT ItemId — the
        // PO line is ERP-fed and routinely has a null ItemId; Item's natural key is (TenantEntityId, Code)).
        // IgnoreQueryFilters — Item is company-scoped and may live in an unshared source company.
        var itemCompany = pos[0].TenantEntityId;
        var lineItemCodes = poLines.Values.Select(l => l.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var itemFlagRows = await _db.Items.IgnoreQueryFilters()
            .Where(i => i.TenantEntityId == itemCompany && !i.IsDeleted && lineItemCodes.Contains(i.Code))
            .Select(i => new { i.Code, i.Id, i.IsSerialized, i.IsLotControlled })
            .ToListAsync(ct);
        var itemFlags = itemFlagRows.ToDictionary(i => i.Code, i => i, StringComparer.OrdinalIgnoreCase);

        // R11.1 — serial/lot uniqueness, per item within the company. Fails fast here rather than waiting for
        // the submit-time guard, which only ran after the buyer had already approved.
        await AsnCaptureUniquenessSupport.ValidateRequestAsync(
            _db, excludeAsnId: null, itemCompany, body.Lines, poLines, ct);

        // R5 (TSD R5 Addendum §10.4) — NO over-ship tolerance resolution / no balance consumption at create; the
        // authoritative atomic guard (which needs the tolerance factor) now runs ONCE at final Submit
        // (AsnSubmitExecutor). The create path only persists the Draft + its serial/lot capture.

        var now = DateTime.UtcNow;
        var asnId = Guid.NewGuid();
        var asnNumber = $"ASN-{supplier.SupplierCode}-{now:yyyyMMddHHmmssfff}";

        // The set of POs actually shipped on (distinct PO of the chosen lines). Single-PO → set the scalar FK
        // for back-compat; multi-PO → leave scalar null, the junction is the source of truth.
        var shippedPoIds = poLines.Values.Select(l => l.PurchaseOrderId).Distinct().ToList();

        // R13 (D4) — single warehouse, resolved from the covered PO LINES actually shipped on (warehouse moved to
        // the PO line; the PO header no longer carries it). Mirrors the retired single-ship-to rule; nulls from
        // pre-R13 lines are not treated as a distinct value, so legacy selections are never blocked. Resolves the
        // code (ships to LN on the header), the address FK, and the snapshot the read side renders without a join.
        var warehouse = AsnWarehouseRules.Resolve(
            poLines.Values.Select(l => new AsnWarehouseRules.WarehouseLine(
                l.Warehouse, l.WarehouseAddressId, l.WarehouseAddressSnapshot)));

        var asn = new Asn
        {
            Id = asnId,
            AsnNumber = asnNumber,
            PurchaseOrderId = shippedPoIds.Count == 1 ? shippedPoIds[0] : null,
            // R13 (D4) — the resolved warehouse: the raw code (LN header), the live address FK, and the frozen snapshot.
            Warehouse = warehouse.Code,
            WarehouseAddressId = warehouse.AddressId,
            WarehouseAddressSnapshot = warehouse.Snapshot,
            // R11 (D6/D7) — optional at create; SendForApproval is the gate that requires them.
            InvoiceNo = AsnHeaderRules.NullIfBlank(body.InvoiceNo),
            BillOfLading = AsnHeaderRules.NullIfBlank(body.BillOfLading),
            PackingList = AsnHeaderRules.NullIfBlank(body.PackingList),
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
            SeccodeId = pos[0].SeccodeId,
            // R11.3 — SNAPSHOT the company from the covered PO, like SeccodeId above. Before this the
            // ScopeStampInterceptor filled TenantEntityId from the session's X-Active-Company header (it only
            // stamps when null, so this assignment wins) — session-derived, not document-derived, and the value
            // feeds the LN payload's CompanyCode and the X-Infor-LnCompany routing header. It was correct only
            // because the company query filter hides cross-company POs at create time — an accident, not a rule.
            TenantEntityId = pos[0].TenantEntityId,
            TenantId = pos[0].TenantId,
            CreatedBy = _user.UserCode,
            CreatedOn = now,
        };

        // Junction rows for every shipped PO (also for single-PO so the covered-PO list is always complete).
        foreach (var poId in shippedPoIds)
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

        foreach (var line in body.Lines)
        {
            var pol = poLines[line.PurchaseOrderLineId];
            // Resolve the Item by code (the PO line's ItemId is often null) — also used to backfill AsnLine.ItemId.
            var flags = !string.IsNullOrWhiteSpace(pol.ItemCode) && itemFlags.TryGetValue(pol.ItemCode, out var f) ? f : null;
            var asnLine = new AsnLine
            {
                Id = Guid.NewGuid(),
                AsnId = asnId,
                PurchaseOrderLineId = line.PurchaseOrderLineId,
                ItemId = pol.ItemId ?? flags?.Id,
                ShippedQty = line.ShippedQty,
                BatchNumber = line.BatchNumber,
                ExpiryDate = line.ExpiryDate,
                PositionNo = pol.PositionNo,     // Addendum A4 — snapshot from the source PO line.
                SequenceNo = pol.SequenceNo,
                CreatedBy = _user.UserCode,
                CreatedOn = now,
            };

            // R4 (2026-06-23) — Serial/Lot children. Persist serials only for a serialized item and lots only for
            // a lot-controlled item (the Item XOR guard means at most one applies); the other side is ignored.
            // Draft-stage capture is lenient — full count/uniqueness validation runs on Submit.
            if (flags?.IsSerialized == true && line.Serials is { Count: > 0 })
            {
                foreach (var serial in line.Serials.Where(s => !string.IsNullOrWhiteSpace(s.SerialNumber)))
                    asnLine.Serials.Add(new AsnLineSerial
                    {
                        Id = Guid.NewGuid(),
                        AsnLineId = asnLine.Id,
                        SerialNumber = serial.SerialNumber.Trim(),
                        ExpiryDate = serial.ExpiryDate,
                        CreatedBy = _user.UserCode,
                        CreatedOn = now,
                    });
            }
            else if (flags?.IsLotControlled == true && line.Lots is { Count: > 0 })
            {
                foreach (var lot in line.Lots.Where(l => !string.IsNullOrWhiteSpace(l.LotNo)))
                    asnLine.Lots.Add(new AsnLineLot
                    {
                        Id = Guid.NewGuid(),
                        AsnLineId = asnLine.Id,
                        LotNo = lot.LotNo.Trim(),
                        Qty = lot.Qty,
                        ExpiryDate = lot.ExpiryDate,
                        CreatedBy = _user.UserCode,
                        CreatedOn = now,
                    });
            }

            asn.Lines.Add(asnLine);
        }

        _db.Asns.Add(asn);

        // R4 (2026-06-23) — rebind any files uploaded DURING creation (ownerEntityType='Staging' under the client's
        // StagingKey) onto this ASN, in the SAME transaction. The rebinder only touches staging rows already stamped
        // with the supplier's seccode at upload time, so cross-supplier keys can't leak in.
        await _rebinder.RebindAsync(body.StagingKey, null, asnId, asn.SeccodeId, now, ct);

        // R4 (2026-06-26) — Addendum §6.2 / §6.5, Component 3 (PO Confirmation Gate). Enforce the ship-gate for
        // EVERY covered PO before any cumulative mutation: a PO that has not reached the supplier's confirmation
        // threshold blocks ASN creation (incl. this Draft save), UNLESS the caller holds PurchaseOrder.OverrideGate
        // and supplied a non-empty OverrideReason — in which case an audited override row is written and shipping
        // proceeds (UC-PO-09). The audit row commits in the SAME SaveChanges below.
        PoConfirmationGateEnforcer.Enforce(
            _db, pos, supplier.PoConfirmationMode, body.OverrideReason, _user, asnId, asnNumber, now);

        // R5 (TSD R5 Addendum §10.4) — NO balance consumption at create. A Draft ASN does NOT touch
        // shippedQtyToDate; the authoritative atomic over-ship guard moved to final Submit (the Approve→Submit
        // path in AsnSubmitExecutor). The advisory over-ship allowance is still surfaced read-only on the DTO
        // (AsnDtoBuilder), but the create path never rejects/consumes — Submit is the single point of truth.
        await _db.SaveChangesAsync(ct);   // ASN + junction + lines + rebound attachments. NO ERP post (Draft only).

        return await AsnDtoBuilder.BuildAsync(_db, asnId, ct, _fulfilment.OverShipAllowanceRounding);
    }
}
