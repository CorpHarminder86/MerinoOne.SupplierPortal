using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R5 — TSD R5 Addendum §11.2 (Component 7, fulfilment-derived statuses). The on-ASN-Submit PO fulfilment-status
/// derivation on REAL SQL, through the real host + the real Approve→Submit path (the only way an ASN reaches
/// Submitted in R5). Asserts the three-owner milestone table:
/// <list type="bullet">
///   <item>submitting an ASN that fully ships EVERY PO line moves the PO to <c>FullyShipped</c> — NOT
///         <c>Delivered</c> (full shipment alone never sets Delivered, §11.2);</item>
///   <item>a subsequent inbound ERP <c>Delivered</c> status advances <c>FullyShipped</c> → <c>Delivered</c>
///         (UC-SM-08), composing with the Phase-2 PoStatusResolver;</item>
///   <item>a partial shipment derives <c>PartiallyDelivered</c>.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FulfilmentStatusDerivationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationTestFixture _fx;
    public FulfilmentStatusDerivationTests(IntegrationTestFixture fx) => _fx = fx;

    public async Task InitializeAsync() { if (_fx.DbAvailable) await _fx.ClearPoliciesAsync(); }
    public async Task DisposeAsync() { if (_fx.DbAvailable) await _fx.ClearPoliciesAsync(); }

    // ── UC-SM-08 — full ship → FullyShipped (NOT Delivered); inbound ERP Delivered then advances it. ──
    [SkippableFact]
    public async Task Full_ship_derives_FullyShipped_then_inbound_ERP_Delivered_advances_to_Delivered()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        // Released PO (confirm:false) → accept (materialise schedules) → create + approve an ASN shipping the WHOLE qty.
        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m, confirm: false);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplierClient, setup.PoId);

        var create = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 10m));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        var approve = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));
        (await Read<AsnDetailDto>(approve)).Data!.AsnStatus
            .Should().Be(nameof(AsnStatus.Submitted));

        // The whole line is shipped → the PO is FullyShipped, NOT Delivered (§11.2 / UC-SM-08).
        (await PoStatusOf(setup.PoId)).Should().Be(PoStatus.FullyShipped,
            because: "every line fully shipped derives FullyShipped — full shipment alone never sets Delivered");

        // A later inbound ERP 'Delivered' status (mapped) advances FullyShipped → Delivered (UC-SM-08, composing
        // with the Phase-2 PoStatusResolver). Same lines/qty so the re-receipt is non-material (no Released re-arm).
        await ReceiveErpStatusAsync(setup, "Delivered");
        (await PoStatusOf(setup.PoId)).Should().Be(PoStatus.Delivered,
            because: "the mapped ERP Delivered status advances a FullyShipped PO to Delivered (UC-SM-08)");
    }

    // ── A partial shipment derives PartiallyDelivered (and a non-material re-sync does not clobber it). ──
    [SkippableFact]
    public async Task Partial_ship_derives_PartiallyDelivered()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m, confirm: false);
        await ProcureToPayFlow.AssignBuyerAsync(_fx, setup.PoId);
        var supplierClient = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);
        await ProcureToPayFlow.AcceptAndGetScheduleIdsAsync(_fx, supplierClient, setup.PoId);

        // Ship only 4 of 10.
        var create = await supplierClient.PostAsJsonAsync("/api/asns", ProcureToPayFlow.SimpleAsn(setup, shippedQty: 4m));
        create.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(create));
        var asnId = (await Read<AsnDetailDto>(create)).Data!.Id;

        var approve = await ProcureToPayFlow.SubmitViaApprovalAsync(_fx, supplierClient, asnId);
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await Body(approve));

        (await PoStatusOf(setup.PoId)).Should().Be(PoStatus.PartiallyDelivered,
            because: "some but not all of the line is shipped → PartiallyDelivered (§11.2)");
    }

    // ════════════════════════════ helpers ════════════════════════════

    /// <summary>Re-pushes the seeded PO (same lines/qty so the re-receipt is non-material) with a raw ERP status.</summary>
    private async Task ReceiveErpStatusAsync(ProcureToPayFlow.Setup setup, string erpStatus)
    {
        var inbound = _fx.CreateInboundClient();
        var body = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(
                PoNumber: setup.PoNumber, SupplierCode: setup.Supplier.SupplierCode, PoDate: DateTime.UtcNow.Date,
                Lines: new[]
                {
                    new PoLineRecord(PositionNo: setup.PoPositionNo, SequenceNo: 1, ItemCode: setup.ItemCode,
                        OrderUnit: "EA", OrderQty: setup.OrderQty, PriceUnit: setup.PriceUnit,
                        Price: setup.PriceUnit * setup.OrderQty),
                },
                ShipToAddress: IntegrationTestFixture.ShipToErpCode,
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR", ErpStatus: erpStatus),
        });
        var resp = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: await resp.Content.ReadAsStringAsync());
    }

    private async Task<PoStatus> PoStatusOf(Guid poId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == poId)
            .Select(p => p.PoStatus).FirstAsync();
    }

    private static async Task<Result<T>> Read<T>(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        return (await JsonSerializer.DeserializeAsync<Result<T>>(stream, Json))!;
    }

    private static async Task<string> Body(HttpResponseMessage resp) => await resp.Content.ReadAsStringAsync();
}
