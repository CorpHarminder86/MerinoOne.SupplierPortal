using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// Fast, DB-less FluentValidation tests for the inbound transactional validators. These run in the
/// <see cref="MerinoOne.SupplierPortal.Application.Common.Behaviours.ValidationBehaviour{TRequest,TResponse}"/>
/// pipeline BEFORE the handler — a failure here is the 400 the controller returns. The validators are
/// dependency-free, so we instantiate them directly.
/// </summary>
public class InboundValidatorTests
{
    private static readonly IReadOnlySet<Guid> NoBoundCompanies = new HashSet<Guid>();

    private static UpsertGoodsReceiptStatusCommand GrnStatusCmd(params GrnStatusRecord[] receipts)
        => new(new PushGrnStatusRequest("2000", receipts), NoBoundCompanies, null);

    // -------------------- UpsertGoodsReceiptStatusCommandValidator --------------------

    [Fact]
    public void GrnStatus_valid_payload_passes()
    {
        var validator = new UpsertGoodsReceiptStatusCommandValidator();

        var result = validator.Validate(GrnStatusCmd(
            new GrnStatusRecord("GRN-1", nameof(MerinoOne.SupplierPortal.Domain.Enums.GrnStatus.GrnApproved))));

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void GrnStatus_unknown_enum_is_rejected()
    {
        var validator = new UpsertGoodsReceiptStatusCommandValidator();

        var result = validator.Validate(GrnStatusCmd(
            new GrnStatusRecord("GRN-1", "TotallyNotAStatus")));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Unknown grnStatus"));
    }

    [Fact]
    public void GrnStatus_empty_receipt_list_is_rejected()
    {
        var validator = new UpsertGoodsReceiptStatusCommandValidator();

        var result = validator.Validate(GrnStatusCmd(/* none */));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GrnStatus_over_cap_batch_is_rejected()
    {
        var validator = new UpsertGoodsReceiptStatusCommandValidator();
        var tooMany = Enumerable.Range(0, 1001)
            .Select(i => new GrnStatusRecord($"GRN-{i}", "GrnApproved"))
            .ToArray();

        var result = validator.Validate(GrnStatusCmd(tooMany));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("1 and 1000"));
    }

    [Fact]
    public void GrnStatus_blank_grnNumber_is_rejected()
    {
        var validator = new UpsertGoodsReceiptStatusCommandValidator();

        var result = validator.Validate(GrnStatusCmd(new GrnStatusRecord("", "GrnApproved")));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GrnStatus_blank_company_code_is_rejected()
    {
        var validator = new UpsertGoodsReceiptStatusCommandValidator();
        var cmd = new UpsertGoodsReceiptStatusCommand(
            new PushGrnStatusRequest("", new[] { new GrnStatusRecord("GRN-1", "GrnApproved") }),
            NoBoundCompanies, null);

        var result = validator.Validate(cmd);

        result.IsValid.Should().BeFalse();
    }

    // -------------------- UpsertGoodsReceiptsCommandValidator (second inbound validator) --------------------

    private static UpsertGoodsReceiptsCommand GoodsReceiptsCmd(string company, params GoodsReceiptRecord[] recs)
        => new(new PushGoodsReceiptsRequest(company, recs), NoBoundCompanies, null);

    [Fact]
    public void GoodsReceipts_valid_payload_passes()
    {
        var validator = new UpsertGoodsReceiptsCommandValidator();

        var result = validator.Validate(GoodsReceiptsCmd("2000",
            new GoodsReceiptRecord("GRN-1", "PO-1", PoPositionNo: 10, ReceivedQty: 5)));

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void GoodsReceipts_empty_list_is_rejected()
    {
        var validator = new UpsertGoodsReceiptsCommandValidator();

        var result = validator.Validate(GoodsReceiptsCmd("2000" /* no receipts */));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GoodsReceipts_over_cap_batch_is_rejected()
    {
        var validator = new UpsertGoodsReceiptsCommandValidator();
        var tooMany = Enumerable.Range(0, 1001)
            .Select(i => new GoodsReceiptRecord($"GRN-{i}", "PO-1", 10, 1))
            .ToArray();

        var result = validator.Validate(GoodsReceiptsCmd("2000", tooMany));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GoodsReceipts_missing_poNumber_is_rejected()
    {
        var validator = new UpsertGoodsReceiptsCommandValidator();

        var result = validator.Validate(GoodsReceiptsCmd("2000",
            new GoodsReceiptRecord("GRN-1", "", PoPositionNo: 10, ReceivedQty: 5)));

        result.IsValid.Should().BeFalse();
    }

    // -------------------- UpsertPurchaseOrdersCommandValidator — supplier-identity one-of (flows 1-4) --------------------

    private static UpsertPurchaseOrdersCommand PoCmd(params PoRecord[] orders)
        => new(new PushPurchaseOrdersRequest("2000", orders), NoBoundCompanies, null);

    // R13 — the header ship-to is retired and warehouse moved to the LINE. The supplier-identity one-of tests
    // isolate the header rule with NO lines (an empty Lines array fires no per-line warehouse rule); the warehouse
    // tests supply a single Line(...) so the per-line Warehouse NotEmpty/MaxLength rule is what is exercised.
    private static PoRecord Po(string? supplierCode = null, string? erpSupplierCode = null,
                               IReadOnlyList<PoLineRecord>? lines = null)
        => new(PoNumber: "PO-1", SupplierCode: supplierCode, PoDate: DateTime.UtcNow.Date,
               Lines: lines ?? Array.Empty<PoLineRecord>(), ErpSupplierCode: erpSupplierCode);

    // R13 — one inbound line carrying a warehouse (the per-line required field). ItemCode is valid so the per-line
    // warehouse rule is the one under test.
    private static PoLineRecord Line(string? warehouse)
        => new(PositionNo: 10, SequenceNo: 1, ItemCode: "ITM", OrderUnit: "EA", OrderQty: 1, PriceUnit: 1, Price: 1,
               Warehouse: warehouse);

    [Fact] // flow 2
    public void Po_with_only_supplierCode_passes()
    {
        var result = new UpsertPurchaseOrdersCommandValidator().Validate(PoCmd(Po(supplierCode: "S0001")));
        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact] // flow 1
    public void Po_with_only_erpSupplierCode_passes()
    {
        var result = new UpsertPurchaseOrdersCommandValidator().Validate(PoCmd(Po(erpSupplierCode: "ERP-9")));
        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact] // flow 3 — both is valid at the validator; the handler applies erpCode priority
    public void Po_with_both_identifiers_passes()
    {
        var result = new UpsertPurchaseOrdersCommandValidator().Validate(PoCmd(Po(supplierCode: "S0001", erpSupplierCode: "ERP-9")));
        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact] // flow 4 — neither is rejected
    public void Po_with_neither_supplier_identifier_is_rejected()
    {
        var result = new UpsertPurchaseOrdersCommandValidator().Validate(PoCmd(Po()));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Either supplierCode or erpSupplierCode is required"));
    }

    [Fact] // whitespace counts as absent (flow 4)
    public void Po_with_blank_supplier_identifiers_is_rejected()
    {
        var result = new UpsertPurchaseOrdersCommandValidator().Validate(PoCmd(Po(supplierCode: "   ", erpSupplierCode: " ")));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Either supplierCode or erpSupplierCode is required"));
    }

    [Fact] // R13 (D1) — Warehouse is required PER LINE. Blank/whitespace counts as absent → error on "warehouse".
    public void Po_line_with_blank_warehouse_is_rejected()
    {
        var result = new UpsertPurchaseOrdersCommandValidator()
            .Validate(PoCmd(Po(supplierCode: "S0001", lines: new[] { Line("  ") })));
        result.IsValid.Should().BeFalse(because: "warehouse is mandatory on every inbound PO line");
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("warehouse", StringComparison.OrdinalIgnoreCase),
            because: "the failure is on the per-line warehouse rule");
    }

    [Fact] // R13 (D1) — warehouse is capped at 50 per line to match the column width.
    public void Po_line_with_overlong_warehouse_is_rejected()
    {
        var result = new UpsertPurchaseOrdersCommandValidator()
            .Validate(PoCmd(Po(supplierCode: "S0001", lines: new[] { Line(new string('W', 51)) })));
        result.IsValid.Should().BeFalse(because: "warehouse is capped at 50");
    }

    [Fact] // R13 — a well-formed per-line warehouse passes; guards against the required rule being over-broad.
    public void Po_line_with_valid_warehouse_passes()
    {
        var result = new UpsertPurchaseOrdersCommandValidator()
            .Validate(PoCmd(Po(supplierCode: "S0001", lines: new[] { Line("WH-01") })));
        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }
}
