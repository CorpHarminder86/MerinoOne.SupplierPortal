using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// ASN serial/lot capture + submit validation through the REAL host. A PO with a serialized line + a lot line
/// is pushed inbound; the supplier client then creates an ASN, captures serials/lots, and submits. The happy
/// path persists the children (read back on GET); the negatives (serial count != qty, Σ lot != qty, duplicate
/// serial / lot within a line) are 400s. A save-as-draft round-trips on GET (not 404).
///
/// <para>Runs with the scope gate OFF (money path). Each test owns a fresh tagged PO + supplier.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnSerialLotTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public AsnSerialLotTests(IntegrationTestFixture fx) => _fx = fx;

    // ============================== happy path ==============================

    [SkippableFact]
    public async Task Asn_submit_with_matching_serials_and_lots_persists_and_reads_back()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupPoWithSerialAndLotLinesAsync();
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Serialized line ships 3 (3 serials); lot line ships 40 (two lots summing to 40).
        var create = new CreateAsnRequest(
            PurchaseOrderId: ctx.PoId,
            PurchaseOrderIds: null,
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(2),
            TimeWindow: null, CarrierName: "Carrier", TrackingNumber: "TRK1",
            VehicleNumber: null, DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(ctx.SerialLineId, ShippedQty: 3, BatchNumber: null, ExpiryDate: null,
                    Serials: new List<AsnLineSerialInput> { new("SN-1"), new("SN-2"), new("SN-3") }),
                new(ctx.LotLineId, ShippedQty: 40, BatchNumber: null, ExpiryDate: null,
                    Lots: new List<AsnLineLotInput>
                    {
                        new("LOT-A", 25), new("LOT-B", 15),
                    }),
            });

        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var created = await Read<AsnDetailDto>(createResp);
        created.Success.Should().BeTrue();
        var asnId = created.Data!.Id;

        var submitResp = await supplierClient.PostAsync($"/api/asns/{asnId}/submit", null);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(submitResp));
        var submitted = await Read<AsnDetailDto>(submitResp);
        submitted.Data!.AsnStatus.Should().Be(nameof(AsnStatus.Submitted));

        // Read back: serials + lots persisted on their lines.
        var getResp = await supplierClient.GetAsync($"/api/asns/{asnId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await Read<AsnDetailDto>(getResp);

        var serLine = detail.Data!.Lines.Single(l => l.PurchaseOrderLineId == ctx.SerialLineId);
        serLine.Serials!.Select(s => s.SerialNumber).Should().BeEquivalentTo(new[] { "SN-1", "SN-2", "SN-3" });

        var lotLine = detail.Data!.Lines.Single(l => l.PurchaseOrderLineId == ctx.LotLineId);
        lotLine.Lots!.Select(x => x.LotNo).Should().BeEquivalentTo(new[] { "LOT-A", "LOT-B" });
        lotLine.Lots!.Sum(x => x.Qty).Should().Be(40m);
    }

    // ============================== save-as-draft round-trips ==============================

    [SkippableFact]
    public async Task Asn_saved_as_draft_is_returned_on_get_not_404()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupPoWithSerialAndLotLinesAsync();
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = SingleSerialLineAsn(ctx, shippedQty: 2, serials: new[] { "D-1", "D-2" });
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(createResp));
        var asnId = (await Read<AsnDetailDto>(createResp)).Data!.Id;

        // No submit — the draft must still be fetchable.
        var getResp = await supplierClient.GetAsync($"/api/asns/{asnId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK, because: "a saved draft ASN must be retrievable, not 404");
        var detail = await Read<AsnDetailDto>(getResp);
        detail.Success.Should().BeTrue();
        detail.Data!.AsnStatus.Should().Be(nameof(AsnStatus.Draft));
        detail.Data!.Lines.Single().Serials!.Select(s => s.SerialNumber).Should().BeEquivalentTo(new[] { "D-1", "D-2" });
    }

    // ============================== negatives (expect 400) ==============================

    [SkippableFact]
    public async Task Asn_submit_rejects_serial_count_not_equal_to_qty()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupPoWithSerialAndLotLinesAsync();
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // ships 3 but only 2 serials → count mismatch on submit.
        var create = SingleSerialLineAsn(ctx, shippedQty: 3, serials: new[] { "X-1", "X-2" });
        var asnId = (await Read<AsnDetailDto>(await PostOk(supplierClient, create))).Data!.Id;

        var submitResp = await supplierClient.PostAsync($"/api/asns/{asnId}/submit", null);
        submitResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a serialized line must carry exactly ShippedQty serials");
        (await Read<AsnDetailDto>(submitResp)).Errors.Should().Contain(e => e.Contains("serial"));
    }

    [SkippableFact]
    public async Task Asn_submit_rejects_lot_sum_not_equal_to_qty()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupPoWithSerialAndLotLinesAsync();
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // lot line ships 40 but Σ lot qty = 30 → mismatch on submit.
        var create = new CreateAsnRequest(
            PurchaseOrderId: ctx.PoId, PurchaseOrderIds: null,
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1),
            TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(ctx.LotLineId, ShippedQty: 40, BatchNumber: null, ExpiryDate: null,
                    Lots: new List<AsnLineLotInput> { new("L-A", 20), new("L-B", 10) }),
            });
        var asnId = (await Read<AsnDetailDto>(await PostOk(supplierClient, create))).Data!.Id;

        var submitResp = await supplierClient.PostAsync($"/api/asns/{asnId}/submit", null);
        submitResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "Σ(lot qty) must equal ShippedQty for a lot-controlled line");
        (await Read<AsnDetailDto>(submitResp)).Errors.Should().Contain(e => e.Contains("lot", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task Asn_create_rejects_duplicate_serial_within_line()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupPoWithSerialAndLotLinesAsync();
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Duplicate serial WITHIN the line — rejected at the Create input layer (400), never reaching the DB index.
        var create = SingleSerialLineAsn(ctx, shippedQty: 2, serials: new[] { "DUP", "DUP" });
        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a serial number may appear only once within a line");
    }

    [SkippableFact]
    public async Task Asn_create_rejects_duplicate_lot_within_line()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var ctx = await SetupPoWithSerialAndLotLinesAsync();
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        var create = new CreateAsnRequest(
            PurchaseOrderId: ctx.PoId, PurchaseOrderIds: null,
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1),
            TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(ctx.LotLineId, ShippedQty: 40, BatchNumber: null, ExpiryDate: null,
                    Lots: new List<AsnLineLotInput> { new("SAME", 20), new("SAME", 20) }),
            });

        var createResp = await supplierClient.PostAsJsonAsync("/api/asns", create);
        createResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: "a lot number may appear only once within a line");
    }

    // ============================== setup helpers ==============================

    private sealed record PoCtx(Guid PoId, Guid SerialLineId, Guid LotLineId, string PoNumber);

    /// <summary>
    /// Pushes a PO (inbound) with a serialized line (pos 10) + a lot line (pos 20) for a fresh tagged supplier,
    /// grants the Supplier-role user read+write on its seccode, and returns the PO + line ids for ASN building.
    /// </summary>
    private async Task<PoCtx> SetupPoWithSerialAndLotLinesAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var supplier = await _fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);

        var serialItem = await _fx.CreateItemAsync($"ASN-SER-{tag}", isSerialized: true);
        var lotItem = await _fx.CreateItemAsync($"ASN-LOT-{tag}", isLotControlled: true);

        var inbound = _fx.CreateInboundClient();
        var poNumber = $"PO-ASN-{tag}";
        var poBody = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(
                PoNumber: poNumber, SupplierCode: supplier.SupplierCode, PoDate: DateTime.UtcNow.Date,
                Lines: new[]
                {
                    new PoLineRecord(PositionNo: 10, SequenceNo: 1, ItemCode: serialItem.ItemCode,
                        OrderUnit: "EA", OrderQty: 10, PriceUnit: 1, Price: 100),
                    new PoLineRecord(PositionNo: 20, SequenceNo: 2, ItemCode: lotItem.ItemCode,
                        OrderUnit: "KG", OrderQty: 100, PriceUnit: 1, Price: 5),
                },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        });
        var poResp = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", poBody);
        poResp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(poResp));

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines)
            .FirstAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
        var serialLineId = po.Lines.Single(l => l.PositionNo == 10).Id;
        var lotLineId = po.Lines.Single(l => l.PositionNo == 20).Id;

        // R4 (2026-06-26) — Phase 2 PO confirmation gate (§6.2): a Released PO under the default AcceptToShip mode
        // BLOCKS ASN creation. These serial/lot tests exercise the ASN capture path, not the gate, so confirm the PO
        // (→ Accepted) here as the supplier would before shipping, keeping the ship-gate open.
        po.PoStatus = PoStatus.Accepted;
        po.AcceptedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return new PoCtx(po.Id, serialLineId, lotLineId, poNumber);
    }

    private static CreateAsnRequest SingleSerialLineAsn(PoCtx ctx, decimal shippedQty, string[] serials)
        => new(
            PurchaseOrderId: ctx.PoId, PurchaseOrderIds: null,
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1),
            TimeWindow: null, CarrierName: null, TrackingNumber: null, VehicleNumber: null,
            DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(ctx.SerialLineId, ShippedQty: shippedQty, BatchNumber: null, ExpiryDate: null,
                    Serials: serials.Select(s => new AsnLineSerialInput(s)).ToList()),
            });

    private static async Task<HttpResponseMessage> PostOk(HttpClient client, CreateAsnRequest body)
    {
        var resp = await client.PostAsJsonAsync("/api/asns", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(resp));
        return resp;
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
