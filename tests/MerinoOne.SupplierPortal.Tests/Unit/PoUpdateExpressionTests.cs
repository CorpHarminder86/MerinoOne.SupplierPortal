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

    private static PurchaseOrderLineInputDoc Line(int pos, int seq, string? date) => new(pos, seq, date);

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

    // ── D6 / D8 — line set ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Undated_lines_are_filtered_off_the_wire()
    {
        var detail = Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, Dec29), Line(20, 1, null), Line(30, 1, Dec30)));

        var lines = detail.GetProperty("Lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        lines.Select(l => l.GetProperty("LineNo").GetString()).Should().Equal("10", "30");
    }

    [Fact]
    public void A_po_with_no_dated_lines_still_posts_with_an_empty_array()
    {
        // D8 — header-only payload. LN still learns the outcome; nothing is silently dropped.
        var detail = Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, null), Line(20, 1, null)));

        detail.GetProperty("Lines").ValueKind.Should().Be(JsonValueKind.Array);
        detail.GetProperty("Lines").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void A_single_line_stays_an_array()
    {
        // D-R9-13 — without the [ ... ] constructor JSONata collapses a one-element result to a bare object.
        var detail = Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, Dec29)));

        detail.GetProperty("Lines").ValueKind.Should().Be(JsonValueKind.Array);
        detail.GetProperty("Lines").GetArrayLength().Should().Be(1);
    }

    // ── D5 — line ids are strings ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Line_ids_go_on_the_wire_as_json_strings()
    {
        var line = Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, Dec29))).GetProperty("Lines")[0];

        line.GetProperty("LineNo").ValueKind.Should().Be(JsonValueKind.String);
        line.GetProperty("SeqNo").ValueKind.Should().Be(JsonValueKind.String);
        line.GetProperty("LineNo").GetString().Should().Be("10");
        line.GetProperty("SeqNo").GetString().Should().Be("1");
    }

    // ── D3 — POStatus literals ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OutboxTransactionType.PoAccept, "Accepted")]
    [InlineData(OutboxTransactionType.PoReject, "Rejected")]
    public void Each_transaction_stamps_its_own_POStatus(string transactionType, string expected)
        => Request(transactionType, Doc(Line(10, 1, Dec29))).GetProperty("POStatus").GetString().Should().Be(expected);

    [Fact]
    public void Accept_and_reject_differ_only_in_POStatus()
    {
        // The three PO_Update expressions are meant to be the same text bar one literal. If this fails,
        // someone edited one and not the others — which is how the reject payload silently drifts.
        var doc = Doc(Line(10, 1, Dec29), Line(20, 2, Dec29));
        var accept = LnJson.CanonicalWrite(Request(OutboxTransactionType.PoAccept, doc).GetRawText());
        var reject = LnJson.CanonicalWrite(Request(OutboxTransactionType.PoReject, doc).GetRawText());

        reject.Should().Be(accept.Replace("\"Accepted\"", "\"Rejected\""));
    }

    // ── D9 — company code ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Company_code_is_carried_from_the_input_document()
        => Request(OutboxTransactionType.PoAccept, Doc(Line(10, 1, Dec29)))
            .GetProperty("CompanyCode").GetString().Should().Be("2000");

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
