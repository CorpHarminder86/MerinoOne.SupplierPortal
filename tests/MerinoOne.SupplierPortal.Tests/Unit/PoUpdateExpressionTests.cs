using System.Text.Json;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R12 — the PO_Update expression harness. Replaces the PO cases in
/// <c>LnRequestExpressionParityTests</c>, which lose their baseline when D13 deletes the compiled C# PO
/// payload builders: with nothing to compare against, the shipped expression is asserted directly against a
/// fixture input document.
///
/// <para>DB-free on purpose — the parity tests are <c>SkippableFact</c> behind a SQL fixture, and the wire
/// contract is too load-bearing to be silently skipped on a machine with no test database.</para>
/// </summary>
public class PoUpdateExpressionTests
{
    private readonly LnDefaultExpressions _catalog = new();
    private readonly LnMappingService _svc = new();

    private static PurchaseOrderInputDoc Doc(params PurchaseOrderLineInputDoc[] lines)
    {
        var list = lines.ToList();
        return new PurchaseOrderInputDoc(
            Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PoNumber: "PUR000323",
            PoStatus: "Accepted",
            ErpStatus: null,
            AcknowledgmentAt: null,
            AcceptedAt: "2026-07-29T09:00:00.0000000Z",
            RejectionReason: null,
            CompanyCode: "2000",
            HeaderDeliveryDate: PoLineDocumentAssembler.HeaderDeliveryDate(list),
            Lines: list,
            ResponseContext: new PoResponseContextInputDoc("Accept", null, null));
    }

    private static PurchaseOrderLineInputDoc Line(int pos, int seq, string? date) => new(pos, seq, date, 5m, "EA");

    private const string Dec29 = "2026-12-29T11:00:00Z";
    private const string Dec30 = "2026-12-30T11:00:00Z";

    private JsonElement Request(string transactionType, PurchaseOrderInputDoc doc)
    {
        var result = _svc.Evaluate(_catalog.TryGet(transactionType)!.RequestExpr, LnJson.SerializeInputDoc(doc));
        result.Ok.Should().BeTrue($"expression must evaluate: {result.Error}");
        return JsonDocument.Parse(result.OutputJson!).RootElement.GetProperty("PurchaseOrderDetail").Clone();
    }

    // ── D7 — header delivery dates ────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_lines_sharing_one_date_emit_both_header_nodes()
    {
        var detail = Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, Dec29), Line(20, 1, Dec29)));

        detail.GetProperty("PlannedDeliveryDate").GetString().Should().Be(Dec29);
        detail.GetProperty("ConfirmedReceiptDate").GetString().Should().Be(Dec29);
    }

    [Fact]
    public void Lines_disagreeing_on_date_omit_both_header_nodes()
    {
        var detail = Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, Dec29), Line(20, 1, Dec30)));

        detail.TryGetProperty("PlannedDeliveryDate", out _).Should().BeFalse();
        detail.TryGetProperty("ConfirmedReceiptDate", out _).Should().BeFalse();
    }

    [Fact]
    public void A_single_undated_line_omits_both_header_nodes()
    {
        // Fail-safe: a header date that is untrue of ANY line is worse than none, because LN applies it to
        // the whole order.
        var detail = Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, Dec29), Line(20, 1, null)));

        detail.TryGetProperty("PlannedDeliveryDate", out _).Should().BeFalse();
        detail.TryGetProperty("ConfirmedReceiptDate", out _).Should().BeFalse();
    }

    // ── 2026-08-06 — accept/reject go HEADER-ONLY (LN rejects the un-quantified line node) ────────────

    [Fact]
    public void Accept_and_reject_carry_no_lines_at_all()
    {
        // LN's PO_Update now requires a numeric Quantity on every Lines[] entry; accept/reject have no
        // business need to push per-line data, so their expressions dropped Lines[] entirely — the shape
        // proven Success against the live LN on 2026-08-06 (audits 8996/8999).
        var doc = Doc(Line(10, 1, Dec29), Line(20, 2, Dec29));

        Request(OutboxTransactionType.PoAccept, doc).TryGetProperty("Lines", out _).Should().BeFalse();
        Request(OutboxTransactionType.PoReject, doc).TryGetProperty("Lines", out _).Should().BeFalse();
    }

    // ── D3 — POStatus literals ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OutboxTransactionType.PoAccept, "Accepted")]
    [InlineData(OutboxTransactionType.PoReject, "Rejected")]
    public void Each_transaction_stamps_its_own_POStatus(string transactionType, string expected)
        => Request(transactionType, Doc(Line(10, 1, Dec29))).GetProperty("POStatus").GetString().Should().Be(expected);

    // ── Reject — Remarks carries the portal-typed rejection reason ────────────────────────────────────

    [Fact]
    public void Reject_maps_the_rejection_reason_to_Remarks()
    {
        var doc = Doc(Line(10, 1, Dec29)) with
        {
            ResponseContext = new PoResponseContextInputDoc("Reject", null, "price too high"),
        };

        Request(OutboxTransactionType.PoReject, doc).GetProperty("Remarks").GetString().Should().Be("price too high");
    }

    // ── D9 — company code ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Company_code_is_carried_from_the_input_document()
        => Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, Dec29)))
            .GetProperty("CompanyCode").GetString().Should().Be("2000");

    // ── Negotiation request — Quantity / UOM (poNegotiation-v3) ───────────────────────────────────────

    private JsonElement NegotiationRequest(params PurchaseOrderLineInputDoc[] lines)
    {
        var list = lines.ToList();
        var doc = new PoNegotiationInputDoc(
            Id: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            PoNumber: "PUR000323",
            NegotiationId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            SubmittedAt: "2026-08-06T09:00:00.0000000Z",
            NegotiationStatus: "Approved",
            CompanyCode: "2000",
            HeaderDeliveryDate: PoLineDocumentAssembler.HeaderDeliveryDate(list),
            Lines: list,
            NegotiationLines: Array.Empty<PoNegotiationLineInputDoc>());
        var result = _svc.Evaluate(
            _catalog.TryGet(OutboxTransactionType.PoNegotiationApprove)!.RequestExpr, LnJson.SerializeInputDoc(doc));
        result.Ok.Should().BeTrue($"expression must evaluate: {result.Error}");
        return JsonDocument.Parse(result.OutputJson!).RootElement.GetProperty("PurchaseOrderDetail").Clone();
    }

    [Fact]
    public void Negotiation_lines_carry_quantity_but_never_a_unit_key()
    {
        // The builder has already overlaid negotiatedQty onto orderQty (D10) — the expression maps it 1:1.
        // No unit key on purpose: LN's validator named only 'Quantity', and no LN response has ever named a
        // unit attribute on the PO_Update line node.
        var line = NegotiationRequest(new PurchaseOrderLineInputDoc(10, 1, Dec29, 2m, "PC")).GetProperty("Lines")[0];

        line.GetProperty("Quantity").ValueKind.Should().Be(JsonValueKind.String);
        line.GetProperty("Quantity").GetString().Should().Be("2");
        line.TryGetProperty("UOM", out _).Should().BeFalse();
        line.TryGetProperty("Uom", out _).Should().BeFalse();
    }

    // $formatNumber, NOT $string — orderQty is decimal(18,4) and STJ preserves scale, so $string emits
    // "4.0000" and flips to scientific notation ("0e+0", "-2.5e+0") on zero-with-scale and negatives:
    // the exact "must be numeric" failure class LN 500'd with on 2026-08-06.
    [Theory]
    [InlineData("4.0000", "4")]
    [InlineData("0.0000", "0")]
    [InlineData("-2.5000", "-2.5")]
    [InlineData("2.5", "2.5")]
    [InlineData("0.0001", "0.0001")]
    [InlineData("1000000.0000", "1000000")]
    public void Negotiation_quantity_is_plain_notation_at_storage_scale(string stored, string expected)
    {
        var line = NegotiationRequest(new PurchaseOrderLineInputDoc(10, 1, Dec29, decimal.Parse(stored), "EA"))
            .GetProperty("Lines")[0];

        line.GetProperty("Quantity").GetString().Should().Be(expected);
    }

    [Fact]
    public void Negotiation_quantity_survives_canonicalisation_on_the_wire()
    {
        // The dispatcher writes LnJson.CanonicalWrite(output) — assert the canonical wire text, not just
        // the raw JSONata output, so a formatter regression cannot hide behind canonicalisation.
        var detail = NegotiationRequest(new PurchaseOrderLineInputDoc(10, 1, Dec29, 4.0000m, "EA"));
        var canonical = LnJson.CanonicalWrite(detail.GetRawText());

        canonical.Should().Contain("\"Quantity\":\"4\"");
        canonical.Should().NotContain("e+", because: "scientific notation is the 'must be numeric' failure class");
    }

    // ── D6 / D8 / D5 — the line set now lives ONLY on the negotiation expression ─────────────────────

    [Fact]
    public void Negotiation_undated_lines_are_filtered_off_the_wire()
    {
        var lines = NegotiationRequest(Line(10, 1, Dec29), Line(20, 1, null), Line(30, 1, Dec30))
            .GetProperty("Lines").EnumerateArray().ToList();

        lines.Should().HaveCount(2);
        lines.Select(l => l.GetProperty("LineNo").GetString()).Should().Equal("10", "30");
    }

    [Fact]
    public void Negotiation_with_no_dated_lines_still_posts_with_an_empty_array()
    {
        // D8 — header-only payload. LN still learns the outcome; nothing is silently dropped.
        var detail = NegotiationRequest(Line(10, 1, null), Line(20, 1, null));

        detail.GetProperty("Lines").ValueKind.Should().Be(JsonValueKind.Array);
        detail.GetProperty("Lines").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Negotiation_single_line_stays_an_array()
    {
        // D-R9-13 — without the [ ... ] constructor JSONata collapses a one-element result to a bare object.
        var detail = NegotiationRequest(Line(10, 1, Dec29));

        detail.GetProperty("Lines").ValueKind.Should().Be(JsonValueKind.Array);
        detail.GetProperty("Lines").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void Negotiation_line_ids_go_on_the_wire_as_json_strings()
    {
        var line = NegotiationRequest(Line(10, 1, Dec29)).GetProperty("Lines")[0];

        line.GetProperty("LineNo").ValueKind.Should().Be(JsonValueKind.String);
        line.GetProperty("SeqNo").ValueKind.Should().Be(JsonValueKind.String);
        line.GetProperty("LineNo").GetString().Should().Be("10");
        line.GetProperty("SeqNo").GetString().Should().Be("1");
    }

    // ── Response mapping ──────────────────────────────────────────────────────────────────────────────

    private const string SuccessBody = """
        { "PurchaseOrder": [ { "Header": { "OrderNo": "PUR000323", "Status": "Success", "Remarks": "" },
                               "Line": [ { "LineNo": "10", "SeqNo": "1", "Status": "Success", "Remarks": "" } ] } ] }
        """;

    private LnOutboundAck ParseResponse(string body)
    {
        var mapped = _svc.Evaluate(_catalog.TryGet(OutboxTransactionType.PoAccept)!.ResponseExpr, body);
        mapped.Ok.Should().BeTrue($"response expression must evaluate: {mapped.Error}");
        var (ack, errors) = LnClosedContract.Parse(mapped.OutputJson);
        ack.Should().NotBeNull($"response must satisfy the closed contract: {string.Join(" ", errors)}");
        return ack!;
    }

    [Fact]
    public void Response_maps_order_number_to_erp_key()
    {
        var ack = ParseResponse(SuccessBody);

        ack.ErpKey.Should().Be("PUR000323");
        ack.ErpStatus.Should().Be("Success");
        ack.Message.Should().BeNull("a blank Remarks with no line failures must not produce an empty message");
    }

    [Fact]
    public void Response_surfaces_header_remarks()
    {
        var ack = ParseResponse("""
            { "PurchaseOrder": [ { "Header": { "OrderNo": "PUR000323", "Status": "Error", "Remarks": "order is closed" },
                                   "Line": [] } ] }
            """);

        ack.ErpStatus.Should().Be("Error");
        ack.Message.Should().Contain("order is closed");
    }

    [Fact]
    public void Response_folds_failing_lines_into_the_message_without_failing_the_row()
    {
        // A2 — Header.Status alone decides pass/fail; a header "Success" hiding a rejected line would
        // otherwise be invisible.
        var ack = ParseResponse("""
            { "PurchaseOrder": [ { "Header": { "OrderNo": "PUR000323", "Status": "Success", "Remarks": "" },
                                   "Line": [ { "LineNo": "10", "SeqNo": "1", "Status": "Success", "Remarks": "" },
                                             { "LineNo": "20", "SeqNo": "1", "Status": "Error", "Remarks": "no such line" } ] } ] }
            """);

        ack.ErpStatus.Should().Be("Success");
        ack.Message.Should().Contain("20/1");
    }
}
