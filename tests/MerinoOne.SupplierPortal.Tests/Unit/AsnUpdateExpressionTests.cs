using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R16 (2026-08-10) — the ASN_Update expression harness. Replaces
/// <c>LnRequestExpressionParityTests.AsnPost_parity_*</c>, whose baseline (byte-equality with
/// <c>AsnOutboundPayloadBuilder</c>) stopped being the truth the moment the real LN contract landed: the wire
/// body wraps in <c>ASNDetail</c>, renames two date nodes, lower-cases the driver block and ships numerics as
/// strings. With nothing left to be at parity WITH, the shipped expression is asserted directly.
///
/// <para>DB-free on purpose, exactly like <see cref="PoUpdateExpressionTests"/> — the parity tests are
/// <c>SkippableFact</c> behind a SQL fixture, and a wire contract is too load-bearing to be silently skipped
/// on a machine with no test database.</para>
/// </summary>
public class AsnUpdateExpressionTests
{
    private readonly LnDefaultExpressions _catalog = new();
    private readonly LnMappingService _svc = new();

    private const string Expiry = "2026-08-31T00:00:00.0000000";
    private const string Shipped = "2026-09-05T07:24:00.0000000Z";

    private static AsnInputDoc Doc(params AsnLineInputDoc[] lines) => new(
        Id: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        AsnNumber: "ASN-S0013-20260810073024924",
        CompanyCode: "2000",
        SupplierBp: "BUS000048",
        ExpectedDeliveryDate: Expiry,
        TimeWindow: "9:00 to 10:00 AM",
        CarrierName: "DTDC",
        TrackingNumber: "5566778899",
        VehicleNumber: "DL104567",
        DriverName: "Shashank",
        DriverPhone: "98989898",
        Warehouse: "AMSWH",
        CreateDate: Shipped,
        ShipmentDate: Shipped,
        InvoiceNo: "INV-1",
        BillOfLading: "bil123",
        PackingList: "Packing1234",
        Notes: "handle with care",
        AsnStatus: "Submitted",
        ErpCode: null,
        Lines: lines.ToList());

    private static AsnLineInputDoc Line(
        int? pos = 40, int? seq = 1, decimal qty = 1m, string? batch = "test batch",
        IReadOnlyList<AsnSerialInputDoc>? serials = null, IReadOnlyList<AsnLotInputDoc>? lots = null) => new(
        PoNumber: "PUR000339",
        PoOrigin: "purchase",
        PositionNo: pos,
        SequenceNo: seq,
        ItemCode: "90000003329146-F-601",
        ShippedQty: qty,
        ShippedQtyInvUnit: qty,
        Uom: "PCS",
        BatchNumber: batch,
        ExpiryDate: Expiry,
        Serials: serials,
        Lots: lots);

    private JsonElement Request(AsnInputDoc doc)
    {
        var result = _svc.Evaluate(_catalog.TryGet(OutboxTransactionType.AsnPost)!.RequestExpr, LnJson.SerializeInputDoc(doc));
        result.Ok.Should().BeTrue($"expression must evaluate: {result.Error}");
        return JsonDocument.Parse(result.OutputJson!).RootElement.GetProperty("ASNDetail").Clone();
    }

    private LnOutboundAck Response(string body)
    {
        var result = _svc.Evaluate(_catalog.TryGet(OutboxTransactionType.AsnPost)!.ResponseExpr, body);
        result.Ok.Should().BeTrue($"expression must evaluate: {result.Error}");
        var (ack, errors) = LnClosedContract.Parse(result.OutputJson);
        ack.Should().NotBeNull($"mapped output must satisfy the closed contract: {string.Join(" ", errors)}");
        return ack!;
    }

    // ── request: header shape ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Header_uses_the_LN_key_names_including_the_lower_camel_driver_block()
    {
        var d = Request(Doc(Line()));

        d.GetProperty("CompanyCode").GetString().Should().Be("2000");
        d.GetProperty("AsnNumber").GetString().Should().Be("ASN-S0013-20260810073024924");
        d.GetProperty("SupplierBp").GetString().Should().Be("BUS000048");
        d.GetProperty("Warehouse").GetString().Should().Be("AMSWH");
        // LN's own spelling on the way in — lower camel, and `vechicalNo` is its typo, not ours.
        d.GetProperty("timeWindow").GetString().Should().Be("9:00 to 10:00 AM");
        d.GetProperty("carrierName").GetString().Should().Be("DTDC");
        d.GetProperty("trackingNo").GetString().Should().Be("5566778899");
        d.GetProperty("vechicalNo").GetString().Should().Be("DL104567");
        d.GetProperty("driverName").GetString().Should().Be("Shashank");
        d.GetProperty("driverPhone").GetString().Should().Be("98989898");
    }

    [Fact]
    public void Date_nodes_are_renamed_to_the_LN_contract()
    {
        var d = Request(Doc(Line()));

        d.GetProperty("ShippingDate").GetString().Should().Be(Shipped);
        d.GetProperty("ASNCreationDate").GetString().Should().Be(Shipped);
        d.TryGetProperty("ShipmentDate", out _).Should().BeFalse();
        d.TryGetProperty("CreateDate", out _).Should().BeFalse();
    }

    [Fact]
    public void Fields_with_no_slot_in_the_LN_contract_are_not_emitted()
    {
        var d = Request(Doc(Line()));

        // InvoiceNo is captured by the portal but LN's ASN header has no such node.
        d.TryGetProperty("InvoiceNo", out _).Should().BeFalse();
        // The mirror gap: LN accepts PackingSlip, the portal has nothing to fill it from.
        d.TryGetProperty("PackingSlip", out _).Should().BeFalse();
        d.GetProperty("Lines")[0].TryGetProperty("ShippedQty_InvUnit", out _).Should().BeFalse();
    }

    [Fact]
    public void Null_header_optionals_are_omitted_not_nulled()
    {
        var doc = Doc(Line()) with { CarrierName = null, TrackingNumber = null, Notes = null, Warehouse = null };
        var d = Request(doc);

        d.TryGetProperty("carrierName", out _).Should().BeFalse();
        d.TryGetProperty("trackingNo", out _).Should().BeFalse();
        d.TryGetProperty("Notes", out _).Should().BeFalse();
        d.TryGetProperty("Warehouse", out _).Should().BeFalse();
    }

    // ── request: line shape ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Line_numerics_ship_as_strings()
    {
        var line = Request(Doc(Line(pos: 40, qty: 12.5m))).GetProperty("Lines")[0];

        line.GetProperty("PositionNo").ValueKind.Should().Be(JsonValueKind.String);
        line.GetProperty("PositionNo").GetString().Should().Be("40");
        line.GetProperty("ShippedQty").ValueKind.Should().Be(JsonValueKind.String);
        line.GetProperty("ShippedQty").GetString().Should().Be("12.5");
    }

    [Fact]
    public void Quantity_sheds_the_sql_decimal_scale()
    {
        // proc.AsnLine.shippedQty is decimal(18,4), so the input document carries "2.0000". Stringifying that
        // literal would put "2.0000" on the wire where LN's own sample shows "1".
        Request(Doc(Line(qty: 2.0000m))).GetProperty("Lines")[0].GetProperty("ShippedQty").GetString().Should().Be("2");
    }

    [Fact]
    public void SequenceNo_is_always_zero_never_the_po_line_sequence()
    {
        // Pinned by two live probes on 2026-08-10, each refuting one of the alternatives:
        //   "1" -> whinh301: "The Sequence field must be empty in ASN Lines." (header created, LINE refused)
        //   ""  -> request validation: "Attr. 'SequenceNo', Value '': the value must be numeric."
        // "0" is the only value both layers accept.
        Request(Doc(Line(seq: 7))).GetProperty("Lines")[0].GetProperty("SequenceNo").GetString().Should().Be("0");
        Request(Doc(Line(seq: null))).GetProperty("Lines")[0].GetProperty("SequenceNo").GetString().Should().Be("0");
    }

    [Fact]
    public void PoOrigin_is_the_LN_literal_not_the_portals_own_classification()
    {
        // proc.PurchaseOrder.poOrigin is a portal vocabulary ("PurchaseOrder"/"EP"/"Requisition"/"Manual",
        // NULL on replicated POs). LN's PoOrigin is its order-origin domain. Passing the former through would
        // send LN a value it has never seen — or, far more often, nothing at all.
        Request(Doc(Line())).GetProperty("Lines")[0].GetProperty("PoOrigin").GetString().Should().Be("purchase");
    }

    [Fact]
    public void Batch_and_expiry_are_lower_camel_on_the_line()
    {
        var line = Request(Doc(Line())).GetProperty("Lines")[0];

        line.GetProperty("batch").GetString().Should().Be("test batch");
        line.GetProperty("expiry").GetString().Should().Be(Expiry);
    }

    [Fact]
    public void Serials_and_lots_survive_as_arrays_and_vanish_when_absent()
    {
        var withChildren = Request(Doc(Line(
            serials: new[] { new AsnSerialInputDoc("SER-001", Expiry) },
            lots: new[] { new AsnLotInputDoc("LOT-9", 5m, null) }))).GetProperty("Lines")[0];

        // D-R9-13 — a single child must still be an ARRAY, never collapsed to a bare object.
        withChildren.GetProperty("Serials").ValueKind.Should().Be(JsonValueKind.Array);
        withChildren.GetProperty("Serials")[0].GetProperty("Serial").GetString().Should().Be("SER-001");
        withChildren.GetProperty("Lots").ValueKind.Should().Be(JsonValueKind.Array);
        withChildren.GetProperty("Lots")[0].GetProperty("LotNo").GetString().Should().Be("LOT-9");
        withChildren.GetProperty("Lots")[0].GetProperty("Qty").GetString().Should().Be("5");

        var bare = Request(Doc(Line())).GetProperty("Lines")[0];
        bare.TryGetProperty("Serials", out _).Should().BeFalse();
        bare.TryGetProperty("Lots", out _).Should().BeFalse();
    }

    [Fact]
    public void Every_line_reaches_the_wire()
    {
        Request(Doc(Line(pos: 10), Line(pos: 20), Line(pos: 30)))
            .GetProperty("Lines").GetArrayLength().Should().Be(3);
    }

    // ── response ──────────────────────────────────────────────────────────────────────────────────────

    private static string Envelope(string headerStatus, string lnAsnNumber, string lineStatus, string lineRemarks = "")
        => $$"""
        {
          "ASN": [
            {
              "Header": {
                "CompanyCode": "2000",
                "PortalAsnNumber": "ASN-S0013-20260810073024923",
                "Warehouse": "AMSWH",
                "LnASNNumber": "{{lnAsnNumber}}",
                "Status": "{{headerStatus}}",
                "Remarks": ""
              },
              "Lines": [
                { "PoNumber": "PUR000339", "PositionNo": "40", "ShippedQty": "1",
                  "Status": "{{lineStatus}}", "Remarks": "{{lineRemarks}}", "ReturnID": "" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void LnASNNumber_becomes_the_erp_key()
    {
        var ack = Response(Envelope("Success", "INB00063", "Success"));

        ack.ErpKey.Should().Be("INB00063");
        ack.ErpStatus.Should().Be("Success");
    }

    [Fact]
    public void A_rejected_line_under_a_successful_header_reports_PartialFailure()
    {
        // The 2026-08-10 probe verbatim: LN created INB00063 and refused the line. Acking that would leave the
        // portal believing stock shipped that is not on the LN ASN.
        var ack = Response(Envelope("Success", "INB00063", "fail",
            "Error In whinh301 Updaton: :The Sequence field must be empty in ASN Lines."));

        ack.ErpStatus.Should().Be("PartialFailure");
        ack.ErpKey.Should().Be("INB00063");
        ack.Message.Should().Contain("PUR000339/40").And.Contain("Sequence field must be empty");
    }

    [Fact]
    public void A_rejected_header_keeps_the_contract_satisfiable_without_an_LN_number()
    {
        var ack = Response(Envelope("Error", "", "fail", "no such supplier"));

        ack.ErpStatus.Should().Be("Error");
        // Falls back to the portal's own ASN number so the closed contract still parses — this path never Acks,
        // so the value is never written anywhere as an ERP handle.
        ack.ErpKey.Should().Be("ASN-S0013-20260810073024923");
    }

    [Fact]
    public void The_pinned_response_sample_maps_cleanly()
    {
        // Guards the sample the seeder pins onto responseSampleJson: save-time validation runs the shipped
        // response expression against it, so a sample that cannot map would block every Dynamic flip.
        var ack = Response(_catalog.AsnUpdateResponseSample);

        ack.ErpKey.Should().Be("INB00063");
        ack.ErpStatus.Should().Be("Success");
    }
}
