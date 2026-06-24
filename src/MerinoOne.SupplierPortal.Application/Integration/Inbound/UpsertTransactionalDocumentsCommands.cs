using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

// R4 (2026-06-23) — Transactional DOCUMENT ingestion (create/upsert the live PO / delivery schedule / GRN
// rows). Reuses the InboundUpsertExecutor's TransactionalInboundEntity path (literal-company resolution,
// anti-spoof, endpoint gate, canonical-hash idempotency, SyncLog/IntegrationError, session telemetry). These
// rows are BaseAggregateRoot — seccode (SeccodeId) is copied from the owning supplier (PO) or the PO (schedule /
// GRN) so supplier users see them under the same RLS as the rest of their procurement chain. All reads use
// IgnoreQueryFilters (service principal has no seccode/company context) and restrict by tenant + resolved company.

// ============================== Purchase Orders ==============================
public record UpsertPurchaseOrdersCommand(PushPurchaseOrdersRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey) : IRequest<UpsertResultDto>;

public class UpsertPurchaseOrdersCommandValidator : AbstractValidator<UpsertPurchaseOrdersCommand>
{
    public UpsertPurchaseOrdersCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Orders).NotEmpty().Must(o => o == null || o.Count <= 1000);
        RuleForEach(x => x.Body.Orders).ChildRules(o =>
        {
            o.RuleFor(r => r.PoNumber).NotEmpty().MaximumLength(60);
            // Supplier identity: one of erpSupplierCode / supplierCode is required (flow 4 reject). When both
            // are present erpSupplierCode wins in the handler — see PoRecord. Each is length-capped when present.
            o.RuleFor(r => r.SupplierCode).MaximumLength(40);
            o.RuleFor(r => r.ErpSupplierCode).MaximumLength(40);
            o.RuleFor(r => r)
                .Must(r => !string.IsNullOrWhiteSpace(r.SupplierCode) || !string.IsNullOrWhiteSpace(r.ErpSupplierCode))
                .WithMessage("Either supplierCode or erpSupplierCode is required.");
            o.RuleFor(r => r.Lines).NotNull();
        });
    }
}

public class UpsertPurchaseOrdersCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertPurchaseOrdersCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertPurchaseOrdersCommand request, CancellationToken ct)
    {
        var recs = request.Body.Orders;
        // Canonical includes a per-line digest (position/seq/item/qty/price/discount) — NOT just Lines.Count — so a
        // follow-up push that adds a line (e.g. position 10 seq 2 alongside an existing seq 1) hashes differently and
        // is NOT short-circuited as a duplicate by the no-header idempotency fallback. Line natural key = (position, seq).
        var canonical = recs.Select(r =>
            $"{r.PoNumber.Trim().ToUpperInvariant()}|{r.ErpSupplierCode?.Trim()}|{r.SupplierCode?.Trim()}|{r.PoDate:O}|{r.PoStatus}|{r.CurrencyCode}|" +
            string.Join(";", r.Lines.Select(l => $"{l.PositionNo}/{l.SequenceNo}/{l.ItemCode}/{l.OrderQty}/{l.PriceUnit}/{l.Price}/{l.DiscountAmount}")));
        var codes = recs.Select(r => r.PoNumber.Trim());
        return exec.ExecuteAsync(TransactionalInboundEntity.Po, request.Body.CompanyCode, request.BoundCompanyIds,
            request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);

            // Resolve owning suppliers → Id + seccode for the PO's RLS owner. A row identifies its supplier by
            // EITHER erpSupplierCode (matched on Supplier.ErpCode) OR supplierCode (matched on Supplier.SupplierCode);
            // erpSupplierCode takes priority when both are present (it is the ERP's authoritative key). Validator
            // already rejects rows with neither — the per-row guard below is defensive. We collect the codes we'll
            // actually look up (erp-codes from erp rows, supplier-codes only from rows NOT using erp) and build two
            // case-insensitive maps in one query, de-duping by First() exactly as the legacy single-key path did.
            var erpCodes = recs.Where(r => !string.IsNullOrWhiteSpace(r.ErpSupplierCode))
                .Select(r => r.ErpSupplierCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var supCodes = recs.Where(r => string.IsNullOrWhiteSpace(r.ErpSupplierCode) && !string.IsNullOrWhiteSpace(r.SupplierCode))
                .Select(r => r.SupplierCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var matchedSuppliers = await db.Suppliers.IgnoreQueryFilters()
                .Where(s => !s.IsDeleted && s.TenantId == tenantId
                    && (supCodes.Contains(s.SupplierCode) || (s.ErpCode != null && erpCodes.Contains(s.ErpCode))))
                .Select(s => new { s.SupplierCode, s.ErpCode, s.Id, s.SeccodeId }).ToListAsync(token);

            var supByCode = matchedSuppliers
                .GroupBy(s => s.SupplierCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var supByErp = matchedSuppliers.Where(s => !string.IsNullOrWhiteSpace(s.ErpCode))
                .GroupBy(s => s.ErpCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Master resolution maps (resolve-or-leave-null; keep snapshot strings).
            var curCodes = recs.Where(r => !string.IsNullOrWhiteSpace(r.CurrencyCode)).Select(r => r.CurrencyCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var curMap = curCodes.Count == 0 ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                : (await db.Currencies.IgnoreQueryFilters().Where(c => !c.IsDeleted && c.TenantId == tenantId && curCodes.Contains(c.Code))
                    .Select(c => new { c.Code, c.Id }).ToListAsync(token)).ToDictionary(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase);

            var itemCodes = recs.SelectMany(r => r.Lines).Where(l => !string.IsNullOrWhiteSpace(l.ItemCode)).Select(l => l.ItemCode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var itemMap = itemCodes.Count == 0 ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                : (await db.Items.IgnoreQueryFilters().Where(i => !i.IsDeleted && i.TenantEntityId == sourceId && itemCodes.Contains(i.Code))
                    .Select(i => new { i.Code, i.Id }).ToListAsync(token)).ToDictionary(i => i.Code, i => i.Id, StringComparer.OrdinalIgnoreCase);

            var taxCodes = recs.SelectMany(r => r.Lines).Where(l => !string.IsNullOrWhiteSpace(l.TaxCode)).Select(l => l.TaxCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var taxMap = taxCodes.Count == 0 ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                : (await db.Taxes.IgnoreQueryFilters().Where(t => !t.IsDeleted && t.TenantEntityId == sourceId && taxCodes.Contains(t.Code))
                    .Select(t => new { t.Code, t.Id }).ToListAsync(token)).ToDictionary(t => t.Code, t => t.Id, StringComparer.OrdinalIgnoreCase);

            // Existing POs (with lines) for upsert-by-PoNumber within the resolved company.
            var poNumbers = recs.Select(r => r.PoNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = (await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines)
                    .Where(p => !p.IsDeleted && p.TenantId == tenantId && p.TenantEntityId == sourceId && poNumbers.Contains(p.PoNumber))
                    .ToListAsync(token))
                .ToDictionary(p => p.PoNumber, StringComparer.OrdinalIgnoreCase);

            foreach (var rec in recs)
            {
                var poNum = rec.PoNumber.Trim();

                // erpSupplierCode wins when present (flows 1 & 3); else supplierCode (flow 2); neither = reject (flow 4).
                var erp = rec.ErpSupplierCode?.Trim();
                var sc = rec.SupplierCode?.Trim();
                var useErp = !string.IsNullOrWhiteSpace(erp);
                var sup = useErp
                    ? (supByErp.TryGetValue(erp!, out var byErp) ? byErp : null)
                    : (!string.IsNullOrWhiteSpace(sc) && supByCode.TryGetValue(sc!, out var byCode) ? byCode : null);
                if (sup is null)
                {
                    var why = useErp ? $"Unknown supplier erpCode '{erp}'."
                        : !string.IsNullOrWhiteSpace(sc) ? $"Unknown supplier code '{sc}'."
                        : "Either supplierCode or erpSupplierCode is required.";
                    results.Add(new RowResult(poNum, RowOutcome.Failed, why)); continue;
                }

                Guid? curId = !string.IsNullOrWhiteSpace(rec.CurrencyCode) && curMap.TryGetValue(rec.CurrencyCode.Trim(), out var ci) ? ci : null;
                var poType = Enum.TryParse<PoType>(rec.PoType, true, out var pt) ? pt : PoType.Material;
                var poStatus = Enum.TryParse<PoStatus>(rec.PoStatus, true, out var ps) ? ps : PoStatus.Released;

                if (existing.TryGetValue(poNum, out var po))
                {
                    po.SupplierId = sup.Id; po.SeccodeId = sup.SeccodeId;
                    po.TenantId = tenantId; po.TenantEntityId = sourceId;
                    po.PoType = poType; po.PoDate = rec.PoDate; po.PoStatus = poStatus;
                    po.PaymentTerms = rec.PaymentTerms; po.DeliveryTerms = rec.DeliveryTerms;
                    po.CurrencyId = curId; po.CurrencyCode = rec.CurrencyCode?.Trim();
                    po.Notes = rec.Notes; po.ErpSyncId = rec.ErpSyncId ?? po.ErpSyncId;
                    po.Version += 1; po.UpdatedBy = "infor:inbound"; po.UpdatedOn = now;
                    SyncLines(db, po, rec, itemMap, taxMap, now);
                    results.Add(new RowResult(poNum, RowOutcome.Updated, null));
                }
                else
                {
                    var po2 = new PurchaseOrder
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = sourceId, SeccodeId = sup.SeccodeId,
                        PoNumber = poNum, SupplierId = sup.Id, PoType = poType, PoDate = rec.PoDate, PoStatus = poStatus,
                        PaymentTerms = rec.PaymentTerms, DeliveryTerms = rec.DeliveryTerms,
                        CurrencyId = curId, CurrencyCode = rec.CurrencyCode?.Trim(),
                        Notes = rec.Notes, ErpSyncId = rec.ErpSyncId, Version = 1,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    };
                    foreach (var l in rec.Lines) po2.Lines.Add(NewLine(l, itemMap, taxMap, now));
                    db.PurchaseOrders.Add(po2);
                    results.Add(new RowResult(poNum, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }

    // Upsert lines by their natural key (PositionNo, SequenceNo): update the matched line, add new ones (does not
    // delete lines absent from the push). A push for the same PositionNo with a different SequenceNo ADDS a new line
    // rather than overwriting the existing seq — mirrors the (purchaseOrderId, positionNo, sequenceNo) unique index.
    //
    // NEW lines are added straight to the DbSet with an explicit PurchaseOrderId — NOT via the tracked po.Lines
    // navigation. Adding to the loaded navigation of an ALREADY-PERSISTED, change-tracked PurchaseOrder makes EF emit
    // a spurious optimistic-concurrency UPDATE against the parent (rowVersion) that fails "0 rows affected" when
    // batched with the child INSERT; the explicit-FK DbSet add inserts the line cleanly without touching the parent's
    // concurrency token. (For a brand-new PO the insert path below still uses the navigation — that aggregate is all-Added.)
    private static void SyncLines(IAppDbContext db, PurchaseOrder po, PoRecord rec,
        IReadOnlyDictionary<string, Guid> itemMap, IReadOnlyDictionary<string, Guid> taxMap, DateTime now)
    {
        var byKey = new Dictionary<(int, int), PurchaseOrderLine>();
        foreach (var existing in po.Lines.Where(l => !l.IsDeleted))
            byKey[(existing.PositionNo, existing.SequenceNo)] = existing; // last-wins guards any legacy dup pre-index
        foreach (var l in rec.Lines)
        {
            if (byKey.TryGetValue((l.PositionNo, l.SequenceNo), out var line))
            {
                Apply(line, l, itemMap, taxMap); line.UpdatedBy = "infor:inbound"; line.UpdatedOn = now;
            }
            else
            {
                var newLine = NewLine(l, itemMap, taxMap, now);
                newLine.PurchaseOrderId = po.Id;
                db.PurchaseOrderLines.Add(newLine);
            }
        }
    }

    private static PurchaseOrderLine NewLine(PoLineRecord l, IReadOnlyDictionary<string, Guid> itemMap, IReadOnlyDictionary<string, Guid> taxMap, DateTime now)
    {
        var line = new PurchaseOrderLine { Id = Guid.NewGuid(), CreatedBy = "infor:inbound", CreatedOn = now };
        Apply(line, l, itemMap, taxMap);
        return line;
    }

    private static void Apply(PurchaseOrderLine line, PoLineRecord l, IReadOnlyDictionary<string, Guid> itemMap, IReadOnlyDictionary<string, Guid> taxMap)
    {
        line.PositionNo = l.PositionNo; line.SequenceNo = l.SequenceNo;
        line.ItemCode = l.ItemCode.Trim(); line.ItemDescription = l.ItemDescription;
        line.ItemId = !string.IsNullOrWhiteSpace(l.ItemCode) && itemMap.TryGetValue(l.ItemCode.Trim(), out var iid) ? iid : null;
        line.OrderUnit = string.IsNullOrWhiteSpace(l.OrderUnit) ? "EA" : l.OrderUnit.Trim();
        line.OrderQty = l.OrderQty; line.PriceUnit = l.PriceUnit; line.Price = l.Price;
        line.DiscountPct = l.DiscountPct; line.DiscountAmount = l.DiscountAmount; line.DeliveryDate = l.DeliveryDate;
        line.TaxCode = l.TaxCode; line.TaxDescription = l.TaxDescription;
        line.TaxId = !string.IsNullOrWhiteSpace(l.TaxCode) && taxMap.TryGetValue(l.TaxCode!.Trim(), out var tid) ? tid : null;
    }
}

// ============================== Delivery Schedules ==============================
public record UpsertDeliverySchedulesCommand(PushDeliverySchedulesRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey) : IRequest<UpsertResultDto>;

public class UpsertDeliverySchedulesCommandValidator : AbstractValidator<UpsertDeliverySchedulesCommand>
{
    public UpsertDeliverySchedulesCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Schedules).NotEmpty().Must(s => s == null || s.Count <= 1000);
        RuleForEach(x => x.Body.Schedules).ChildRules(s => s.RuleFor(r => r.PoNumber).NotEmpty().MaximumLength(60));
    }
}

public class UpsertDeliverySchedulesCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertDeliverySchedulesCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertDeliverySchedulesCommand request, CancellationToken ct)
    {
        var recs = request.Body.Schedules;
        var canonical = recs.Select(r => $"{r.PoNumber.Trim().ToUpperInvariant()}|{r.ProposedDate:O}|{r.TimeWindow}|{r.ScheduleStatus}");
        var codes = recs.Select(r => r.PoNumber.Trim());
        return exec.ExecuteAsync(TransactionalInboundEntity.DeliverySchedule, request.Body.CompanyCode, request.BoundCompanyIds,
            request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);

            var poNumbers = recs.Select(r => r.PoNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var poMap = (await db.PurchaseOrders.IgnoreQueryFilters()
                    .Where(p => !p.IsDeleted && p.TenantId == tenantId && p.TenantEntityId == sourceId && poNumbers.Contains(p.PoNumber))
                    .Select(p => new { p.PoNumber, p.Id, p.SeccodeId }).ToListAsync(token))
                .ToDictionary(p => p.PoNumber, StringComparer.OrdinalIgnoreCase);

            var poIds = poMap.Values.Select(p => p.Id).ToList();
            var existing = await db.DeliverySchedules.IgnoreQueryFilters()
                .Where(d => !d.IsDeleted && d.TenantId == tenantId && poIds.Contains(d.PurchaseOrderId)).ToListAsync(token);

            foreach (var rec in recs)
            {
                var poNum = rec.PoNumber.Trim();
                if (!poMap.TryGetValue(poNum, out var po))
                { results.Add(new RowResult(poNum, RowOutcome.Failed, $"Unknown PO '{poNum}' for the resolved company.")); continue; }

                var status = Enum.TryParse<ScheduleStatus>(rec.ScheduleStatus, true, out var st) ? st : ScheduleStatus.Proposed;
                var row = existing.FirstOrDefault(d => d.PurchaseOrderId == po.Id && d.ProposedDate == rec.ProposedDate);
                if (row is not null)
                {
                    row.TimeWindow = rec.TimeWindow; row.VehicleInfo = rec.VehicleInfo; row.ScheduleStatus = status;
                    row.SeccodeId = po.SeccodeId; row.TenantId = tenantId; row.TenantEntityId = sourceId;
                    row.UpdatedBy = "infor:inbound"; row.UpdatedOn = now;
                    results.Add(new RowResult(poNum, RowOutcome.Updated, null));
                }
                else
                {
                    db.DeliverySchedules.Add(new DeliverySchedule
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = sourceId, SeccodeId = po.SeccodeId,
                        PurchaseOrderId = po.Id, ProposedDate = rec.ProposedDate, TimeWindow = rec.TimeWindow,
                        VehicleInfo = rec.VehicleInfo, ScheduleStatus = status,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(poNum, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}

// ============================== Goods Receipts (create) ==============================
public record UpsertGoodsReceiptsCommand(PushGoodsReceiptsRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey) : IRequest<UpsertResultDto>;

public class UpsertGoodsReceiptsCommandValidator : AbstractValidator<UpsertGoodsReceiptsCommand>
{
    public UpsertGoodsReceiptsCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Receipts).NotEmpty().Must(r => r == null || r.Count <= 1000);
        RuleForEach(x => x.Body.Receipts).ChildRules(r =>
        {
            r.RuleFor(g => g.GrnNumber).NotEmpty().MaximumLength(60);
            r.RuleFor(g => g.PoNumber).NotEmpty().MaximumLength(60);
        });
    }
}

public class UpsertGoodsReceiptsCommandHandler(InboundUpsertExecutor exec) : IRequestHandler<UpsertGoodsReceiptsCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertGoodsReceiptsCommand request, CancellationToken ct)
    {
        var recs = request.Body.Receipts;
        var canonical = recs.Select(r => $"{r.GrnNumber.Trim().ToUpperInvariant()}|{r.PoNumber.Trim()}|{r.PoPositionNo}|{r.ReceivedQty}|{(r.ErpSyncId ?? "").Trim()}");
        var codes = recs.Select(r => r.GrnNumber.Trim());
        return exec.ExecuteAsync(TransactionalInboundEntity.GrnReceipt, request.Body.CompanyCode, request.BoundCompanyIds,
            request.IdempotencyKey, recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);

            // Resolve PO lines by (PoNumber, PositionNo) → lineId + the PO's seccode for the GRN owner.
            var poNumbers = recs.Select(r => r.PoNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pos = await db.PurchaseOrders.IgnoreQueryFilters()
                .Where(p => !p.IsDeleted && p.TenantId == tenantId && p.TenantEntityId == sourceId && poNumbers.Contains(p.PoNumber))
                .Select(p => new { p.Id, p.PoNumber, p.SeccodeId }).ToListAsync(token);
            var poByNumber = pos.ToDictionary(p => p.PoNumber, StringComparer.OrdinalIgnoreCase);
            var poIds = pos.Select(p => p.Id).ToList();
            var lines = await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => !l.IsDeleted && poIds.Contains(l.PurchaseOrderId))
                .Select(l => new { l.Id, l.PurchaseOrderId, l.PositionNo }).ToListAsync(token);
            var lineByPoPos = lines.ToDictionary(l => (l.PurchaseOrderId, l.PositionNo));

            // ASN resolution (optional link) by AsnNumber.
            var asnNums = recs.Where(r => !string.IsNullOrWhiteSpace(r.AsnNumber)).Select(r => r.AsnNumber!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var asnMap = asnNums.Count == 0 ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                : (await db.Asns.IgnoreQueryFilters().Where(a => !a.IsDeleted && a.TenantId == tenantId && asnNums.Contains(a.AsnNumber))
                    .Select(a => new { a.AsnNumber, a.Id }).ToListAsync(token)).ToDictionary(a => a.AsnNumber, a => a.Id, StringComparer.OrdinalIgnoreCase);

            var grnNumbers = recs.Select(r => r.GrnNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = (await db.GoodsReceipts.IgnoreQueryFilters()
                    .Where(g => !g.IsDeleted && g.TenantId == tenantId && g.TenantEntityId == sourceId && grnNumbers.Contains(g.GrnNumber))
                    .ToListAsync(token))
                .ToDictionary(g => g.GrnNumber, StringComparer.OrdinalIgnoreCase);

            foreach (var rec in recs)
            {
                var grnNum = rec.GrnNumber.Trim();
                if (!poByNumber.TryGetValue(rec.PoNumber.Trim(), out var po))
                { results.Add(new RowResult(grnNum, RowOutcome.Failed, $"Unknown PO '{rec.PoNumber}' for the resolved company.")); continue; }
                if (!lineByPoPos.TryGetValue((po.Id, rec.PoPositionNo), out var line))
                { results.Add(new RowResult(grnNum, RowOutcome.Failed, $"PO '{rec.PoNumber}' has no line at position {rec.PoPositionNo}.")); continue; }

                Guid? asnId = !string.IsNullOrWhiteSpace(rec.AsnNumber) && asnMap.TryGetValue(rec.AsnNumber!.Trim(), out var aid) ? aid : null;

                if (existing.TryGetValue(grnNum, out var grn))
                {
                    grn.PurchaseOrderLineId = line.Id; grn.AsnId = asnId;
                    grn.ReceivedQty = rec.ReceivedQty; grn.ShortQty = rec.ShortQty ?? 0; grn.RejectedQty = rec.RejectedQty ?? 0;
                    if (rec.GrnDate.HasValue) grn.GrnDate = rec.GrnDate.Value;
                    grn.ErpSyncId = rec.ErpSyncId ?? grn.ErpSyncId;
                    grn.SeccodeId = po.SeccodeId; grn.TenantId = tenantId; grn.TenantEntityId = sourceId;
                    grn.UpdatedBy = "infor:inbound"; grn.UpdatedOn = now;
                    results.Add(new RowResult(grnNum, RowOutcome.Updated, null));
                }
                else
                {
                    db.GoodsReceipts.Add(new GoodsReceipt
                    {
                        Id = Guid.NewGuid(), TenantId = tenantId, TenantEntityId = sourceId, SeccodeId = po.SeccodeId,
                        GrnNumber = grnNum, PurchaseOrderLineId = line.Id, AsnId = asnId,
                        ReceivedQty = rec.ReceivedQty, ShortQty = rec.ShortQty ?? 0, RejectedQty = rec.RejectedQty ?? 0,
                        GrnDate = rec.GrnDate ?? now, ErpSyncId = rec.ErpSyncId, GrnStatus = GrnStatus.GrnNotApproved,
                        CreatedBy = "infor:inbound", CreatedOn = now
                    });
                    results.Add(new RowResult(grnNum, RowOutcome.Inserted, null));
                }
            }
            return results;
        }
    }
}
