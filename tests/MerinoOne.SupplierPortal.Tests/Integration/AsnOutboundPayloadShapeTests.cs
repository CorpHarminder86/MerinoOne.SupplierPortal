using System.Net;
using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R11 (D15) — pins the ASN outbound WIRE CONTRACT to LN <c>whinh.advanceShipmentNotices</c>.
/// <para>The byte-parity harness proves the JSONata expression and the C# builder agree with EACH OTHER; it
/// cannot tell whether either agrees with what LN expects. This asserts the actual key names, casing and value
/// sources, so a rename shows up as a failing contract test rather than as a silently-dropped OData property.</para>
/// <para>If the Infor/LN team confirms different names, THIS is the test to change first — then the .jsonata and
/// AsnOutboundPayloadBuilder to match.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AsnOutboundPayloadShapeTests
{
    private readonly IntegrationTestFixture _fx;

    public AsnOutboundPayloadShapeTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Approved_asn_emits_the_agreed_ln_wire_contract()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        var setup = await ProcureToPayFlow.SeedPoAsync(_fx, orderQty: 10m);
        var supplier = await _fx.ClientAsAsync(SecurityTestHarness.Users.Supplier, IntegrationTestFixture.CompanyId);

        // Create → Send-for-approval → Approve. Approval is what stamps SubmittedAt (the payload's ShipmentDate)
        // and enqueues the LN post, so only an APPROVED ASN produces a complete document.
        var approve = await ProcureToPayFlow.CreateAndSubmitAsync(_fx, supplier, setup, shippedQty: 4m);
        approve.StatusCode.Should().Be(HttpStatusCode.OK, because: await approve.Content.ReadAsStringAsync());

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asn = await db.Asns.IgnoreQueryFilters()
            .FirstAsync(a => a.PurchaseOrderId == setup.PoId || a.PurchaseOrders.Any(j => j.PurchaseOrderId == setup.PoId));

        // Populate every OPTIONAL header field so all 18 keys are present. The payload omits nulls
        // (WhenWritingNull, unchanged from R9 and covered by the parity test's null-optionals case), so a
        // sparsely-populated ASN would silently under-assert the contract.
        asn.TimeWindow = "09:00 - 10:00";
        asn.VehicleNumber = "MH12AB1234";
        asn.DriverName = "A Driver";
        asn.DriverPhone = "9999999999";
        asn.Notes = "handle with care";
        var sup = await db.Suppliers.IgnoreQueryFilters().FirstAsync(s => s.Id == asn.SupplierId);
        sup.ErpCode = "ERP-BP-001";
        await db.SaveChangesAsync();

        var json = await AsnOutboundPayloadBuilder.BuildJsonAsync(db, asn.Id);
        json.Should().NotBeNull();

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        // ── Header: exactly the agreed key set, in the agreed casing ────────────────────────────────────
        var headerKeys = root.EnumerateObject().Select(p => p.Name).ToList();
        headerKeys.Should().BeEquivalentTo(new[]
        {
            "CompanyCode", "SupplierBp", "AsnNumber", "ExpectedDeliveryDate", "TimeWindow",
            "CarrierName", "TrackingNo", "VehicleNo", "DriverName", "DriverPhone",
            "Warehouse", "CreateDate", "ShipmentDate", "InvoiceNo", "BillOfLading", "PackingList",
            "Notes", "Lines",
        }, because: "the LN wire contract is fixed; a rename must fail here, not silently drop at OData");

        // Renamed keys must be GONE — catches a half-applied rename.
        headerKeys.Should().NotContain("TrackingNumber");
        headerKeys.Should().NotContain("VehicleNumber");

        root.GetProperty("CompanyCode").GetString().Should().Be(IntegrationTestFixture.CompanyCode,
            because: "CompanyCode is the ERP company code, the same value LN sends inbound");
        root.GetProperty("SupplierBp").GetString().Should().Be("ERP-BP-001",
            because: "SupplierBp is the supplier's LN business-partner code (Supplier.ErpCode)");
        root.GetProperty("Warehouse").GetString().Should().Be(IntegrationTestFixture.ShipToErpCode,
            because: "the ASN snapshots the warehouse off the PO LINE it ships against (SeedPoAsync uses ShipToErpCode)");
        root.GetProperty("AsnNumber").GetString().Should().Be(asn.AsnNumber);

        // CreateDate = audit creation instant; ShipmentDate = SubmittedAt, stamped at buyer approval.
        root.GetProperty("CreateDate").GetDateTime().Should().BeCloseTo(asn.CreatedOn, TimeSpan.FromSeconds(1));
        asn.SubmittedAt.Should().NotBeNull(because: "approval stamps it");
        root.GetProperty("ShipmentDate").GetDateTime().Should().BeCloseTo(asn.SubmittedAt!.Value, TimeSpan.FromSeconds(1));

        // The three refs come from the ASN header (the test harness fills them before send).
        root.GetProperty("InvoiceNo").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("BillOfLading").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("PackingList").GetString().Should().NotBeNullOrWhiteSpace();

        // ── Lines ───────────────────────────────────────────────────────────────────────────────────────
        var line = root.GetProperty("Lines").EnumerateArray().Single();
        var lineKeys = line.EnumerateObject().Select(p => p.Name).ToList();
        lineKeys.Should().Contain(new[]
        {
            "PoNumber", "PositionNo", "SequenceNo", "ItemCode", "ShippedQty", "ShippedQty_InvUnit", "Uom",
        });
        lineKeys.Should().NotContain("BatchNumber", because: "the line batch key is now 'Batch'");
        lineKeys.Should().NotContain("ExpiryDate", because: "the line expiry key is now 'Expiry'");

        line.GetProperty("PoNumber").GetString().Should().Be(setup.PoNumber,
            because: "the per-line PO reference is what makes a multi-PO ASN representable to LN");
        line.GetProperty("ShippedQty").GetDecimal().Should().Be(4m);
        line.GetProperty("ShippedQty_InvUnit").GetDecimal().Should().Be(4m,
            because: "D5 — the inventory-unit qty is a pure mirror of ShippedQty, not a conversion");
        line.GetProperty("Uom").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
