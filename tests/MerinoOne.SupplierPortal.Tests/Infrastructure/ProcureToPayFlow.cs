using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MerinoOne.SupplierPortal.Tests.Infrastructure;

/// <summary>
/// Shared procure-to-pay flow scaffolding for the lifecycle suites (ASN→invoice, invoice lifecycle, GRN
/// auto-post + payment). Pushes a single-line PO inbound for a fresh tagged supplier (granting the Supplier
/// API user read+write on its seccode) and exposes the ids the rest of the chain needs, plus a simple
/// single-line ASN request builder.
/// </summary>
public static class ProcureToPayFlow
{
    public sealed record Setup(
        string Tag,
        SecurityTestHarness.SeededSupplier Supplier,
        Guid PoId,
        string PoNumber,
        Guid PoLineId,
        int PoPositionNo,
        string ItemCode,
        decimal OrderQty,
        decimal PriceUnit);

    /// <summary>
    /// Pushes a single-line PO (qty 10 @ price-unit 100, a plain non-serial/non-lot item) for a fresh tagged
    /// supplier, and returns the persisted ids. Grants the Supplier-role user read+write on the new seccode.
    /// </summary>
    public static async Task<Setup> SeedPoAsync(IntegrationTestFixture fx, decimal orderQty = 10m, decimal priceUnit = 100m)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var supplier = await fx.CreateSupplierAsync(tag,
            IntegrationTestFixture.TenantId, IntegrationTestFixture.CompanyId,
            grantUserCode: SecurityTestHarness.Users.Supplier, canWrite: true);

        // A plain item (no serial/lot) so the ASN happy path needs no per-line capture.
        var item = await fx.CreateItemAsync($"P2P-{tag}");

        var inbound = fx.CreateInboundClient();
        var poNumber = $"PO-P2P-{tag}";
        const int positionNo = 10;
        var poBody = new PushPurchaseOrdersRequest(IntegrationTestFixture.CompanyCode, new[]
        {
            new PoRecord(
                PoNumber: poNumber, SupplierCode: supplier.SupplierCode, PoDate: DateTime.UtcNow.Date,
                Lines: new[]
                {
                    new PoLineRecord(PositionNo: positionNo, SequenceNo: 1, ItemCode: item.ItemCode,
                        OrderUnit: "EA", OrderQty: orderQty, PriceUnit: priceUnit, Price: priceUnit * orderQty),
                },
                PoStatus: nameof(PoStatus.Released), CurrencyCode: "INR"),
        });
        var poResp = await inbound.PostAsJsonAsync("/api/integration/inbound/purchase-orders", poBody);
        poResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: await poResp.Content.ReadAsStringAsync());

        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines)
            .FirstAsync(p => p.PoNumber == poNumber && p.TenantId == IntegrationTestFixture.TenantId);
        var line = po.Lines.Single(l => l.PositionNo == positionNo);

        return new Setup(tag, supplier, po.Id, poNumber, line.Id, positionNo, item.ItemCode, orderQty, priceUnit);
    }

    /// <summary>A single-line ASN against the seeded PO line, shipping the full ordered qty.</summary>
    public static CreateAsnRequest SimpleAsn(Setup s, decimal? shippedQty = null)
        => new(
            PurchaseOrderId: s.PoId, PurchaseOrderIds: null,
            ExpectedDeliveryDate: DateTime.UtcNow.Date.AddDays(1),
            TimeWindow: null, CarrierName: "Carrier", TrackingNumber: "TRK",
            VehicleNumber: null, DriverName: null, DriverPhone: null, Notes: null,
            Lines: new List<CreateAsnLineRequest>
            {
                new(s.PoLineId, ShippedQty: shippedQty ?? s.OrderQty, BatchNumber: null, ExpiryDate: null),
            });
}
