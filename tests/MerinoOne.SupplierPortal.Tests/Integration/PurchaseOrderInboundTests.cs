using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// PO inbound ingestion + read-back lifecycle, through the REAL host: a Purchase Order pushed by Infor LN over
/// the X-APIKey scheme (POST /api/integration/inbound/purchase-orders) must persist, and the supplier's
/// authenticated read (GET /api/purchase-orders/{id}) must surface the line columns the procurement chain
/// depends on — currency, taxId (resolved by TaxCode against the company's pushed Tax master), discountPct,
/// and the serial/lot control flags resolved off inv.Item by ItemCode.
///
/// <para>Runs with the scope gate in its money-path OFF state (no flip). Every row carries a per-test tag so it
/// never collides with the shared seed.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PurchaseOrderInboundTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public PurchaseOrderInboundTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Inbound_po_persists_and_readback_resolves_currency_tax_and_serial_lot_flags()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];

        // A tagged supplier (its own G-seccode) under the fixture tenant/company, granting the Supplier-role
        // API user read so the supplier client can GET its own PO once it lands.
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);

        // Two items honouring the inv.Item XOR CHECK: one serialized, one lot-controlled. Seeded under company
        // "2000" so the PO-line read resolves the flags by (TenantEntityId, ItemCode).
        var serialItem = await _fx.CreateItemAsync($"SER-{tag}", isSerialized: true);
        var lotItem = await _fx.CreateItemAsync($"LOT-{tag}", isLotControlled: true);

        // Seed a Tax master so the PO line's taxCode resolves to a Tax FK (taxId). Seeded directly (not via the
        // inbound /taxes endpoint) because the fixture API key does not carry the Integration.Inbound.Tax scope.
        var taxCode = $"GST18-{tag}";
        await _fx.CreateTaxAsync(taxCode, rate: 18m);

        var inbound = _fx.CreateInboundClient();

        // Push the PO with two lines: a serialized line + a lot line, each carrying a taxCode + discountPct.
        var poNumber = $"PO-INB-{tag}";
        var poBody = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(
                PoNumber: poNumber,
                SupplierCode: supplier.SupplierCode,
                PoDate: DateTime.UtcNow.Date,
                Lines: new[]
                {
                    new PoLineRecord(PositionNo: 10, SequenceNo: 1, ItemCode: serialItem.ItemCode,
                        OrderUnit: "EA", OrderQty: 5, PriceUnit: 1, Price: 100,
                        DiscountPct: 10m, TaxCode: taxCode, TaxDescription: "GST 18%"),
                    new PoLineRecord(PositionNo: 20, SequenceNo: 2, ItemCode: lotItem.ItemCode,
                        OrderUnit: "KG", OrderQty: 40, PriceUnit: 1, Price: 25,
                        DiscountPct: 5m, TaxCode: taxCode, TaxDescription: "GST 18%"),
                },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released),
                CurrencyCode: "INR"),
        });

        var poResp = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", poBody);
        poResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var pushResult = await Read<UpsertResultDto>(poResp);
        pushResult.Success.Should().BeTrue();
        pushResult.Data!.Failed.Should().Be(0, because: "the supplier code resolves, so the PO row inserts cleanly");
        pushResult.Data!.Inserted.Should().Be(1);

        // Find the persisted PO id (the inbound DTO returns per-row outcomes keyed by code, not the new Guid).
        Guid poId;
        Guid? expectedTaxId;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters()
                .FirstAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            poId = po.Id;
            po.SupplierId.Should().Be(supplier.SupplierId, because: "the PO's owning supplier resolves by code");
            po.SeccodeId.Should().Be(supplier.SeccodeId, because: "the PO inherits the owning supplier's G-seccode for RLS");
            po.CurrencyCode.Should().Be("INR");

            expectedTaxId = await db.Taxes.IgnoreQueryFilters()
                .Where(t => t.TenantEntityId == IntegrationTestFixture.CompanyId && t.Code == taxCode)
                .Select(t => (Guid?)t.Id).FirstAsync();
        }

        // Supplier reads its own PO. The detail DTO must carry currency + each line's taxId, discountPct, and
        // the serial/lot flags resolved off inv.Item.
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var getResp = await supplierClient.GetAsync($"/api/purchase-orders/{poId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await Read<PurchaseOrderDetailDto>(getResp);
        detail.Success.Should().BeTrue();
        detail.Data!.CurrencyCode.Should().Be("INR");
        detail.Data!.Lines.Should().HaveCount(2);

        var serLine = detail.Data!.Lines.Single(l => l.PositionNo == 10);
        serLine.ItemCode.Should().Be(serialItem.ItemCode);
        serLine.TaxId.Should().Be(expectedTaxId, because: "the line's taxCode resolves to the pushed Tax master FK");
        serLine.DiscountPct.Should().Be(10m);
        serLine.IsSerialized.Should().BeTrue(because: "the line item is serialized in inv.Item");
        serLine.IsLotControlled.Should().BeFalse();

        var lotLine = detail.Data!.Lines.Single(l => l.PositionNo == 20);
        lotLine.ItemCode.Should().Be(lotItem.ItemCode);
        lotLine.TaxId.Should().Be(expectedTaxId);
        lotLine.DiscountPct.Should().Be(5m);
        lotLine.IsLotControlled.Should().BeTrue(because: "the line item is lot-controlled in inv.Item");
        lotLine.IsSerialized.Should().BeFalse();

        // PO LIST surfaces amount (sum of line nets = 100 + 25), currency, and the term strings.
        var listResp = await supplierClient.GetAsync($"/api/purchase-orders?supplierId={supplier.SupplierId}&pageSize=200");
        var list = await Read<MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<PurchaseOrderListItemDto>>(listResp);
        var listRow = list.Data!.Items.Single(p => p.PoNumber == poNumber);
        listRow.TotalAmount.Should().Be(125m, because: "list amount = sum of line net amounts (Price − DiscountAmount)");
        listRow.CurrencyCode.Should().Be("INR");
    }

    /// <summary>
    /// UC-PS-01 (R5 §6.2) — a valid ship-to code resolves: the PO persists with shipToAddressId set + the
    /// point-in-time snapshot columns populated from the seeded CompanyAddress (addressName / erpCode / city / …),
    /// and the read model surfaces the derived CustomerName + the snapshot.
    /// </summary>
    [SkippableFact]
    public async Task Inbound_po_resolves_shipTo_and_snapshots()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var poNumber = $"PO-SHIPTO-{tag}";

        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 10, PriceUnit: 1, Price: 10) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR", ErpStatus: "Released"),
        }));

        Guid poId;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters()
                .FirstAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            poId = po.Id;
            po.ShipToAddressId.Should().Be(IntegrationTestFixture.ShipToAddressId,
                because: "the ship-to code resolved to the seeded CompanyAddress FK");
            po.ErpStatus.Should().Be("Released", because: "the raw ERP status is stored (tracking only)");
            po.ShipTo.Should().NotBeNull(because: "the point-in-time ship-to snapshot is captured at resolution");
            po.ShipTo!.ErpCode.Should().Be(IntegrationTestFixture.ShipToErpCode);
            po.ShipTo!.AddressName.Should().Be("IntTest DC");
            po.ShipTo!.City.Should().Be("Mumbai");
        }

        // Read model: derived CustomerName (TenantEntity.Name — [[r5-consolidation]]) + snapshot fields (display-only).
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var detail = (await Read<PurchaseOrderDetailDto>(await supplierClient.GetAsync($"/api/purchase-orders/{poId}"))).Data!;
        detail.CustomerName.Should().Be("IntTest Co 2000", because: "customer name derives live from the TenantEntity (the company) by tenantEntityId");
        detail.ShipToAddressName.Should().Be("IntTest DC");
        detail.ShipToErpCode.Should().Be(IntegrationTestFixture.ShipToErpCode);
        detail.ShipToCity.Should().Be("Mumbai");
        detail.ShipToState.Should().Be("Maharashtra");
    }

    /// <summary>
    /// UC-PS-02 (R5 §6.2) — an unresolvable ship-to code HARD-FAILS that PO row: Failed=1, the PO is NOT persisted.
    /// </summary>
    [SkippableFact]
    public async Task Inbound_po_unresolvable_shipTo_hard_fails()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, canWrite: true);
        var poNumber = $"PO-BADSHIP-{tag}";

        var body = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 10, PriceUnit: 1, Price: 10) },
                ShipToAddress: $"NO-SUCH-DC-{tag}",
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        });
        var resp = await _fx.CreateInboundClient().PostAsJsonAsync("/api/integration/inbound/purchase-orders", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Read<UpsertResultDto>(resp);
        r.Data!.Failed.Should().Be(1, because: "the ship-to code does not resolve to an active CompanyAddress (UC-PS-02)");
        r.Data!.Inserted.Should().Be(0);
        r.Data!.Rows.Single().Error.Should().Contain("Ship-to code", because: "the failure carries the offending code");

        using var s = _fx.Factory.Services.CreateScope();
        var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PurchaseOrders.IgnoreQueryFilters().AnyAsync(p => p.PoNumber == poNumber))
            .Should().BeFalse(because: "a PO with an unresolvable ship-to is never created");
    }

    /// <summary>
    /// Supplier-identity resolution on the inbound PO push (erpSupplierCode / supplierCode). Covers all four
    /// flows: (1) erpSupplierCode only → resolve by Supplier.ErpCode; (2) supplierCode only → resolve by code
    /// [covered by the test above]; (3) BOTH → erpSupplierCode WINS; (4) NEITHER → 400, nothing persists.
    /// Plus an unknown erpCode → per-row failure (200, Failed=1).
    /// </summary>
    [SkippableFact]
    public async Task Inbound_po_resolves_supplier_by_erpCode_with_priority_over_supplierCode()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];

        // Supplier A carries an ERP code; Supplier B is a decoy that only has a supplier code — used to prove the
        // priority rule (a PO citing A's erpCode AND B's supplierCode must bind A, not B).
        var supA = await _fx.CreateSupplierAsync($"A{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        var supB = await _fx.CreateSupplierAsync($"B{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        var erp = $"ERP-{tag}";
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.Suppliers.IgnoreQueryFilters().FirstAsync(x => x.Id == supA.SupplierId);
            a.ErpCode = erp;
            await db.SaveChangesAsync();
        }

        var inbound = _fx.CreateInboundClient();

        PushPurchaseOrdersRequest Body(string poNum, string? supplierCode, string? erpSupplierCode) =>
            new(IntegrationTestFixture.CompanyCode, new[]
            {
                new PoRecord(
                    PoNumber: poNum,
                    SupplierCode: supplierCode,
                    PoDate: DateTime.UtcNow.Date,
                    Lines: new[]
                    {
                        new PoLineRecord(PositionNo: 10, SequenceNo: 1, ItemCode: $"ITM-{tag}",
                            OrderUnit: "EA", OrderQty: 1, PriceUnit: 1, Price: 1),
                    },
                    ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                    PoStatus: nameof(PoStatus.Released),
                    CurrencyCode: "INR",
                    ErpSupplierCode: erpSupplierCode),
            });

        async Task<Guid?> ResolvedSupplierIdAsync(string poNum)
        {
            using var s = _fx.Factory.Services.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.PurchaseOrders.IgnoreQueryFilters()
                .Where(p => p.PoNumber == poNum && p.TenantId == IntegrationTestFixture.TenantId)
                .Select(p => (Guid?)p.SupplierId).FirstOrDefaultAsync();
        }

        // ---- Flow 1: erpSupplierCode only → resolves Supplier A by ErpCode. ----
        var po1 = $"PO-ERP1-{tag}";
        var resp1 = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(po1, supplierCode: null, erpSupplierCode: erp));
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var r1 = await Read<UpsertResultDto>(resp1);
        r1.Data!.Failed.Should().Be(0);
        r1.Data!.Inserted.Should().Be(1);
        (await ResolvedSupplierIdAsync(po1)).Should().Be(supA.SupplierId, because: "erpSupplierCode matched Supplier.ErpCode");

        // ---- Flow 3: BOTH present → erpSupplierCode WINS (binds A), supplierCode (B) is ignored. ----
        var po3 = $"PO-ERP3-{tag}";
        var resp3 = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(po3, supplierCode: supB.SupplierCode, erpSupplierCode: erp));
        resp3.StatusCode.Should().Be(HttpStatusCode.OK);
        var r3 = await Read<UpsertResultDto>(resp3);
        r3.Data!.Failed.Should().Be(0);
        r3.Data!.Inserted.Should().Be(1);
        (await ResolvedSupplierIdAsync(po3)).Should().Be(supA.SupplierId,
            because: "with both identifiers present, erpSupplierCode takes priority over supplierCode");

        // ---- Unknown erpCode → per-row failure (200, Failed=1, nothing inserted). ----
        var po9 = $"PO-ERP9-{tag}";
        var resp9 = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(po9, supplierCode: null, erpSupplierCode: $"NOPE-{tag}"));
        resp9.StatusCode.Should().Be(HttpStatusCode.OK);
        var r9 = await Read<UpsertResultDto>(resp9);
        r9.Data!.Inserted.Should().Be(0);
        r9.Data!.Failed.Should().Be(1, because: "an unresolvable erpSupplierCode fails that row, not the whole batch");
        (await ResolvedSupplierIdAsync(po9)).Should().BeNull();

        // ---- Flow 4: NEITHER identifier → request rejected by the validator (400), nothing persists. ----
        var po4 = $"PO-ERP4-{tag}";
        var resp4 = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(po4, supplierCode: null, erpSupplierCode: null));
        resp4.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ResolvedSupplierIdAsync(po4)).Should().BeNull();
    }

    /// <summary>
    /// R4 (2026-06-30) — STORAGE line key is positionNo ALONE; sequenceNo is always stored as 1. A second push for
    /// the SAME position with a different sequence FOLDS into the one stored line (last-push attributes win), it does
    /// NOT add a second row — so the (po, positionNo, 1) unique index is never threatened.
    /// </summary>
    [SkippableFact]
    public async Task Inbound_po_second_push_same_position_new_sequence_folds_into_one_line_seq1()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var poNumber = $"PO-SEQ-{tag}";

        PushPurchaseOrdersRequest Body(int positionNo, int sequenceNo, decimal qty, decimal price, decimal discountAmount) =>
            new(IntegrationTestFixture.CompanyCode, new[]
            {
                new PoRecord(
                    PoNumber: poNumber, SupplierCode: supplier.SupplierCode, PoDate: DateTime.UtcNow.Date,
                    Lines: new[]
                    {
                        new PoLineRecord(PositionNo: positionNo, SequenceNo: sequenceNo, ItemCode: $"ITM-{tag}",
                            OrderUnit: "EA", OrderQty: qty, PriceUnit: price, Price: price, DiscountAmount: discountAmount),
                    },
                    ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                    PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR")
            });

        // Push 1: position 10 / seq 1, qty 5, price 100, discount 10 → net 90.
        var resp1 = await inbound_PostAsync(Body(10, 1, qty: 5, price: 100m, discountAmount: 10m));
        (await Read<UpsertResultDto>(resp1)).Data!.Inserted.Should().Be(1);

        // Push 2: SAME position 10 but seq 2, qty 8, price 200, discount 0 → must FOLD into the one position-10 line
        // (replace qty/price), NOT add a second row. The stored seq stays 1.
        var resp2 = await inbound_PostAsync(Body(10, 2, qty: 8, price: 200m, discountAmount: 0m));
        var r2 = (await Read<UpsertResultDto>(resp2)).Data!;
        r2.Failed.Should().Be(0, because: r2.Rows.Count > 0 ? string.Join(" | ", r2.Rows.Select(x => $"{x.Code}/{x.Outcome}/{x.Error}")) : "no failures expected");
        r2.Updated.Should().Be(1, because: "the PO already exists, so push 2 is an in-place update (fold), not an insert");

        Guid poId;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters()
                .FirstAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
            poId = po.Id;
            var lines = await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.PurchaseOrderId == poId && !l.IsDeleted)
                .Select(l => new { l.PositionNo, l.SequenceNo, l.OrderQty, l.Price }).ToListAsync();
            lines.Should().HaveCount(1, because: "the second seq for the same position folds into the one stored line (not added)");
            lines.Single().SequenceNo.Should().Be(1, because: "sequenceNo is ALWAYS stored as 1");
            lines.Single().OrderQty.Should().Be(8m, because: "the later push replaced the qty (last-push attributes win)");
            lines.Single().Price.Should().Be(200m);
        }

        // Header total = the single folded line's net (price 200 − discount 0 = 200).
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var detail = (await Read<PurchaseOrderDetailDto>(await supplierClient.GetAsync($"/api/purchase-orders/{poId}"))).Data!;
        detail.Lines.Should().HaveCount(1);
        detail.TotalAmount.Should().Be(200m);

        Task<HttpResponseMessage> inbound_PostAsync(PushPurchaseOrdersRequest body) =>
            _fx.CreateInboundClient().PostAsJsonAsync("/api/integration/inbound/purchase-orders", body);
    }

    /// <summary>
    /// R4 (2026-06-30) — line-level AdditionalQty + forced sequenceNo=1. orderQty>0 REPLACES the stored qty;
    /// additionalQty≠0 ADDS a SIGNED delta (may reduce) to the current qty; 0/0 is a no-op; both-set is rejected
    /// (400); and sequenceNo is ALWAYS stored as 1 regardless of the pushed value.
    /// </summary>
    [SkippableFact]
    public async Task Inbound_po_additionalQty_replace_add_reduce_noop_and_seq_forced_to_1()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, canWrite: true);
        var poNumber = $"PO-ADDQ-{tag}";

        PushPurchaseOrdersRequest Body(int seq, decimal orderQty, decimal additionalQty) =>
            new(IntegrationTestFixture.CompanyCode, new[]
            {
                new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                    new[] { new PoLineRecord(PositionNo: 10, SequenceNo: seq, ItemCode: $"ITM-{tag}",
                        OrderUnit: "EA", OrderQty: orderQty, PriceUnit: 1, Price: 1, AdditionalQty: additionalQty) },
                    ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                    PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
            });

        async Task<decimal> StoredQtyAsync()
        {
            using var s = _fx.Factory.Services.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var line = await db.PurchaseOrderLines.IgnoreQueryFilters()
                .FirstAsync(l => l.PurchaseOrder!.PoNumber == poNumber && !l.IsDeleted && l.PositionNo == 10);
            line.SequenceNo.Should().Be(1, because: "sequenceNo is ALWAYS stored as 1 regardless of the pushed seq");
            return line.OrderQty;
        }

        // 1) REPLACE — orderQty 100, add 0 (pushed seq 7 → must store as 1).
        await PushAsync(Body(seq: 7, orderQty: 100, additionalQty: 0));
        (await StoredQtyAsync()).Should().Be(100m, because: "orderQty>0 with add 0 replaces the absolute qty");

        // 2) ADD — orderQty 0, add +20 → 120 (cumulative add to the current qty).
        await PushAsync(Body(seq: 3, orderQty: 0, additionalQty: 20));
        (await StoredQtyAsync()).Should().Be(120m, because: "orderQty 0 with add>0 adds to the current qty");

        // 3) REDUCE — orderQty 0, add -50 → 70 (negative delta).
        await PushAsync(Body(seq: 9, orderQty: 0, additionalQty: -50));
        (await StoredQtyAsync()).Should().Be(70m, because: "additionalQty may be negative to reduce the qty");

        // 4) NO-OP — orderQty 0, add 0 → unchanged (70).
        await PushAsync(Body(seq: 1, orderQty: 0, additionalQty: 0));
        (await StoredQtyAsync()).Should().Be(70m, because: "0/0 is a no-op — the qty is left unchanged");

        // 5) BOTH SET → rejected at the validator (400); the qty is untouched.
        var bad = await _fx.CreateInboundClient()
            .PostAsJsonAsync("/api/integration/inbound/purchase-orders", Body(seq: 1, orderQty: 5, additionalQty: 5));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "orderQty and additionalQty are mutually exclusive on a line");
        (await StoredQtyAsync()).Should().Be(70m);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════════
    // Phase 3 — PO-Change Sync Hardening (TSD R4 Addendum §5 / §6.3 / §6.4; UC-PO-06/07, UC-ASN-08/10, DI-01/03).
    // ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DI-01 — the in-place PO-line upsert PRESERVES shipped history across a qty revision. A line carrying
    /// shippedQtyToDate=80 plus a linked AsnLine survives a re-push that changes orderQty: the PO line keeps the
    /// SAME id (no delete-recreate), the cumulative is untouched (80), and the AsnLine FK still resolves to it.
    /// </summary>
    [SkippableFact]
    public async Task PoSync_PreservesShippedQty_AndFks()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var poNumber = $"PO-DI01-{tag}";

        // Push the PO (orderQty 100), then seed shippedQtyToDate=80 + a linked AsnLine directly so we can prove the
        // upsert leaves both intact across a qty revision.
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 100, PriceUnit: 1, Price: 100) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));

        Guid poLineId, asnLineId;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var line = await db.PurchaseOrderLines.IgnoreQueryFilters()
                .FirstAsync(l => l.PurchaseOrder!.PoNumber == poNumber && l.PositionNo == 10);
            poLineId = line.Id;
            line.ShippedQtyToDate = 80m;

            var asn = new Asn
            {
                Id = Guid.NewGuid(), AsnNumber = $"ASN-DI01-{tag}", SupplierId = supplier.SupplierId,
                ExpectedDeliveryDate = DateTime.UtcNow.Date.AddDays(1), AsnStatus = AsnStatus.Submitted,
                SeccodeId = supplier.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
                TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            };
            db.Asns.Add(asn);
            var asnLine = new AsnLine
            {
                Id = Guid.NewGuid(), AsnId = asn.Id, PurchaseOrderLineId = poLineId, ShippedQty = 80m,
                PositionNo = 10, SequenceNo = 1, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            };
            db.AsnLines.Add(asnLine);
            asnLineId = asnLine.Id;
            await db.SaveChangesAsync();
        }

        // Re-push the SAME line (same position 10 / seq 1) with orderQty 100→120 — an in-place UPDATE.
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 120, PriceUnit: 1, Price: 120) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));

        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            // Still exactly one line for the PO, with the SAME id (NOT delete-recreated).
            var lines = await db.PurchaseOrderLines.IgnoreQueryFilters()
                .Where(l => l.PurchaseOrder!.PoNumber == poNumber && !l.IsDeleted).ToListAsync();
            lines.Should().HaveCount(1, because: "the upsert updates in place — it never delete-recreates a line");
            var line = lines[0];
            line.Id.Should().Be(poLineId, because: "the matched line keeps its id (the AsnLine FK depends on it)");
            line.OrderQty.Should().Be(120m, because: "the qty revision was applied in place");
            line.ShippedQtyToDate.Should().Be(80m, because: "the cumulative is preserved across a qty revision (DI-01)");

            // The AsnLine FK still resolves to the same PO line.
            var asnLine = await db.AsnLines.IgnoreQueryFilters().FirstAsync(a => a.Id == asnLineId);
            asnLine.PurchaseOrderLineId.Should().Be(poLineId, because: "the AsnLine FK was not orphaned by the re-push");
        }
    }

    /// <summary>
    /// UC-PO-06 / §6.3-§6.4 — a MATERIAL ERP modify (order qty change) on a confirmed PO updates the line IN PLACE,
    /// resets PoStatus → Released (re-arming the gate, UC-ASN-08), and bumps version.
    /// </summary>
    [SkippableFact]
    public async Task MaterialModify_UpdatesInPlace_ResetsToReleased()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, canWrite: true);
        var poNumber = $"PO-MAT-{tag}";

        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 100, PriceUnit: 1, Price: 100) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));

        Guid poLineId;
        int versionAfterAccept;
        // Move the PO to Accepted (a confirmed PO) so the reset-to-Released is observable.
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines).FirstAsync(p => p.PoNumber == poNumber);
            po.PoStatus = PoStatus.Accepted;
            po.AcceptedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            poLineId = po.Lines.First(l => l.PositionNo == 10).Id;
            versionAfterAccept = po.Version;
        }

        // MATERIAL re-push: orderQty 100→150 on the same line.
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 150, PriceUnit: 1, Price: 150) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Accepted), CurrencyCode: "INR"),   // pushed status is ignored on a material modify
        }));

        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines).FirstAsync(p => p.PoNumber == poNumber);
            po.PoStatus.Should().Be(PoStatus.Released, because: "a material modify re-arms the confirmation gate (UC-PO-06)");
            po.Version.Should().BeGreaterThan(versionAfterAccept, because: "a material modify bumps version");
            var line = po.Lines.Single(l => l.PositionNo == 10);
            line.Id.Should().Be(poLineId, because: "the line was updated in place (not delete-recreated)");
            line.OrderQty.Should().Be(150m, because: "the qty change was applied in place");
        }
    }

    /// <summary>
    /// UC-PO-07 / §6.4 — a NON-material ERP modify (notes only) bumps version but does NOT reset confirmation: a
    /// supplier mid-fulfilment is not frozen.
    /// </summary>
    [SkippableFact]
    public async Task NonMaterialModify_DoesNotResetConfirmation()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, canWrite: true);
        var poNumber = $"PO-NONMAT-{tag}";

        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 100, PriceUnit: 1, Price: 100) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));

        int versionAfterAccept;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.PoNumber == poNumber);
            po.PoStatus = PoStatus.Accepted;
            po.AcceptedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            versionAfterAccept = po.Version;
        }

        // NON-material re-push: SAME qty/price/date; only the header notes + item description differ.
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", ItemDescription: "renamed", OrderUnit: "EA", OrderQty: 100, PriceUnit: 1, Price: 100) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR", Notes: "internal ref updated"),
        }));

        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.PoNumber == poNumber);
            po.PoStatus.Should().Be(PoStatus.Accepted, because: "a non-material modify must NOT reset confirmation (UC-PO-07)");
            po.Version.Should().BeGreaterThan(versionAfterAccept, because: "a non-material modify still bumps version");
        }
    }

    /// <summary>
    /// UC-ASN-10 / §5.3 — an ERP downward revision below the already-shipped cumulative (orderQty 100→60 with 80
    /// shipped) sets the over-shipped flag on the PO-line read model AND auto-blocks further ASNs (the atomic guard
    /// reads orderQty live).
    /// </summary>
    [SkippableFact]
    public async Task DownwardRevision_BelowShipped_FlagsAndBlocks()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);
        var poNumber = $"PO-DOWN-{tag}";

        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 100, PriceUnit: 1, Price: 100) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Accepted), CurrencyCode: "INR"),
        }));

        Guid poId, poLineId;
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines).FirstAsync(p => p.PoNumber == poNumber);
            poId = po.Id;
            var line = po.Lines.First(l => l.PositionNo == 10);
            poLineId = line.Id;
            line.ShippedQtyToDate = 80m;   // 80 already shipped against the order of 100.
            // The inbound push set Accepted on insert, but enum-reset rules only matter for an existing material modify;
            // keep it Accepted so the read-back focuses on the over-ship flag.
            po.PoStatus = PoStatus.Accepted;
            await db.SaveChangesAsync();
        }

        // ERP downward revision: orderQty 100→60 (below the 80 shipped). Material → resets to Released; that's fine —
        // the flag + guard are what UC-ASN-10 asserts.
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 60, PriceUnit: 1, Price: 60) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Accepted), CurrencyCode: "INR"),
        }));

        // The PO-line read model flags the over-shipped line (balance MAX(0,…)=0, ShippedQtyToDate 80 > OrderQty 60).
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        var detail = (await Read<PurchaseOrderDetailDto>(await supplierClient.GetAsync($"/api/purchase-orders/{poId}"))).Data!;
        var lineDto = detail.Lines.Single(l => l.PositionNo == 10);
        lineDto.OrderQty.Should().Be(60m);
        lineDto.ShippedQtyToDate.Should().Be(80m, because: "the cumulative is preserved across the downward revision");
        lineDto.Balance.Should().Be(0m, because: "balance is MAX(0, orderQty − shippedQtyToDate)");
        lineDto.IsOverShippedQtyReduced.Should().BeTrue(because: "orderQty 60 < shippedQtyToDate 80 (UC-ASN-10)");

        // R5 (§10.4) — the over-ship atomic guard MOVED to final Submit (Approve→Submit). So a further ASN can still
        // be saved as a Draft (no guard at create), but the guard (orderQty×factor − shippedQtyToDate ≥ shipQty) fails
        // at submit because 60 − 80 < anything positive. Move the PO back to Accepted so the confirmation GATE doesn't
        // pre-empt the quantity guard, assign a buyer for the approve step, then drive create → submit-via-approval.
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var po = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.Id == poId);
            po.PoStatus = PoStatus.Accepted;
            po.BuyerUserId = SecurityTestHarness.BuyerUserId;   // for the Approve→Submit gate.
            await db.SaveChangesAsync();
        }

        // Enable the over-ship guard for this tenant so the ceiling rejection fires (default-off rollout flag, D3).
        await using var guardOn = await _fx.EnableOverShipGuardAsync();

        var createBody = new
        {
            purchaseOrderId = poId,
            expectedDeliveryDate = DateTime.UtcNow.Date.AddDays(1),
            lines = new[] { new { purchaseOrderLineId = poLineId, shippedQty = 1m } },
        };
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", createBody);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "the Draft create does NOT consume / does NOT guard (the guard moved to Submit, §10.4)");
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        var submitResp = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        submitResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "the order was revised below the shipped cumulative — the atomic guard blocks the ASN at Submit (UC-ASN-10)");
    }

    /// <summary>
    /// UC-PO-01/10 / §6.2 / D1 — AutoAccept ingest wiring. A NEW PO that lands as Released for a supplier in
    /// PoConfirmationMode.AutoAccept is AUTO-STAMPED Accepted + acceptedAt at ingest (immediately shippable, no
    /// manual step) AND the acceptance is enqueued to the ERP outbox. An AcceptToShip supplier stays at Released.
    /// </summary>
    [SkippableFact]
    public async Task AutoAccept_ingest_autostamps_accepted_and_enqueues_acceptance()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];

        // Supplier A — AutoAccept; Supplier B — AcceptToShip (control). Set A's mode directly (the supplier-settings
        // UI is Phase 5; the ingest only reads the column).
        var supAuto = await _fx.CreateSupplierAsync($"A{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        var supManual = await _fx.CreateSupplierAsync($"M{tag}", IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId);
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.Suppliers.IgnoreQueryFilters().FirstAsync(x => x.Id == supAuto.SupplierId);
            a.PoConfirmationMode = PoConfirmationMode.AutoAccept;
            await db.SaveChangesAsync();
        }

        var poAuto = $"PO-AUTO-{tag}";
        var poManual = $"PO-MAN-{tag}";
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poAuto, supAuto.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 10, PriceUnit: 1, Price: 10) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
            new PoRecord(poManual, supManual.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 10, PriceUnit: 1, Price: 10) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));

        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();

            var autoPo = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.PoNumber == poAuto);
            autoPo.PoStatus.Should().Be(PoStatus.Accepted, because: "AutoAccept suppliers are auto-stamped Accepted at ingest (UC-PO-10)");
            autoPo.AcceptedAt.Should().NotBeNull(because: "acceptedAt is stamped at the auto-accept");
            autoPo.AcknowledgmentAt.Should().NotBeNull(because: "the auto-accept also stamps acknowledged");

            // The ERP acceptance was enqueued to the outbox.
            var enqueued = await db.OutboxMessages.IgnoreQueryFilters()
                .AnyAsync(m => m.EntityId == autoPo.Id && m.TransactionType == "PoAccept");
            enqueued.Should().BeTrue(because: "the auto-accept enqueues a PoAccept to the ERP outbox");

            var manualPo = await db.PurchaseOrders.IgnoreQueryFilters().FirstAsync(p => p.PoNumber == poManual);
            manualPo.PoStatus.Should().Be(PoStatus.Released, because: "AcceptToShip suppliers stay at Released — the supplier must confirm (UC-PO-01)");
            manualPo.AcceptedAt.Should().BeNull();
        }
    }

    /// <summary>
    /// UC-PO-06 / §14 — a material modify enqueues a supplier notification (EmailOutbox) carrying the diff + revised
    /// ship balance. The recipient is the supplier's primary contact email.
    /// </summary>
    [SkippableFact]
    public async Task MaterialModify_enqueues_supplier_notification()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var tag = Guid.NewGuid().ToString("N")[..8];
        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId, canWrite: true);
        var contactEmail = $"contact-{tag}@example.com";
        using (var s = _fx.Factory.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SupplierContacts.Add(new MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierContact
            {
                Id = Guid.NewGuid(), SupplierId = supplier.SupplierId, ContactName = "Primary", Email = contactEmail,
                IsPrimary = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var poNumber = $"PO-NOTIFY-{tag}";
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 100, PriceUnit: 1, Price: 100) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));

        // MATERIAL re-push (qty 100→120) → one EmailOutbox row to the primary contact.
        await PushAsync(new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(poNumber, supplier.SupplierCode, DateTime.UtcNow.Date,
                new[] { new PoLineRecord(10, 1, $"ITM-{tag}", OrderUnit: "EA", OrderQty: 120, PriceUnit: 1, Price: 120) },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        }));

        using var scope = _fx.Factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mail = await db2.EmailOutbox.IgnoreQueryFilters()
            .Where(m => m.ToEmail == contactEmail && m.TemplateKey == "PoRevised")
            .OrderByDescending(m => m.CreatedOn).FirstOrDefaultAsync();
        mail.Should().NotBeNull(because: "a material modify enqueues a supplier notification (§14)");
        mail!.Subject.Should().Contain(poNumber);
        mail.HtmlBody.Should().Contain("100→120", because: "the notification carries the qty diff");
    }

    // Pushes a PO body. A fresh Idempotency-Key per push (default a new GUID) ensures a re-push with an
    // IDENTICAL canonical body (e.g. a notes-only change that the line digest does not capture) is processed by
    // the handler rather than short-circuited by the canonical-hash dedup — mirrors a distinct ERP delivery.
    private async Task PushAsync(PushPurchaseOrdersRequest body, string? idempotencyKey = null)
    {
        var client = _fx.CreateInboundClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        var resp = await client.PostAsJsonAsync("/api/integration/inbound/purchase-orders", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var r = await Read<UpsertResultDto>(resp);
        r.Data!.Failed.Should().Be(0,
            because: r.Data.Rows.Count > 0 ? string.Join(" | ", r.Data.Rows.Select(x => $"{x.Code}/{x.Outcome}/{x.Error}")) : "no failures expected");
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }
}
