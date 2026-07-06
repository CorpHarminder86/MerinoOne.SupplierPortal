using FluentAssertions;
using MerinoOne.SupplierPortal.Infrastructure.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R9 (§2.11) — the shared JSONata implementation of R8's IEligibilityGate: STRICT-TRUE, fail closed on
/// null/whitespace/malformed expressions and on non-boolean results — the exact posture of the JsonPath
/// gate it replaces in the B8 retrofit.
/// </summary>
public class JsonataEligibilityGateTests
{
    private readonly JsonataEligibilityGate _gate = new(new LnMappingService());

    private sealed record Snapshot(InvoiceSnap invoice);
    private sealed record InvoiceSnap(string? erpCompany, string? erpTransactionType, string? erpDocumentNo);

    // The converted form of the R8 dot-path array ["invoice.erpCompany","invoice.erpTransactionType","invoice.erpDocumentNo"].
    private const string ConvertedGate =
        "(invoice.erpCompany != null and $trim($string(invoice.erpCompany)) != \"\") and " +
        "(invoice.erpTransactionType != null and $trim($string(invoice.erpTransactionType)) != \"\") and " +
        "(invoice.erpDocumentNo != null and $trim($string(invoice.erpDocumentNo)) != \"\")";

    [Fact]
    public void All_paths_present_is_satisfied()
        => _gate.IsSatisfied(ConvertedGate, new Snapshot(new InvoiceSnap("2000", "1DS", "23063669"))).Should().BeTrue();

    [Theory]
    [InlineData(null, "1DS", "23063669")]
    [InlineData("2000", null, "23063669")]
    [InlineData("2000", "1DS", null)]
    [InlineData("2000", "1DS", "   ")] // whitespace-only — R8 used IsNullOrWhiteSpace; $trim reproduces it
    public void Any_missing_or_blank_path_fails(string? company, string? txType, string? docNo)
        => _gate.IsSatisfied(ConvertedGate, new Snapshot(new InvoiceSnap(company, txType, docNo))).Should().BeFalse();

    [Fact]
    public void Missing_segment_fails_closed()
        => _gate.IsSatisfied(ConvertedGate, new { unrelated = true }).Should().BeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{{{{ not jsonata")]
    public void Blank_or_malformed_expression_fails_closed(string expr)
        => _gate.IsSatisfied(expr, new Snapshot(new InvoiceSnap("2000", "1DS", "X"))).Should().BeFalse();

    [Fact]
    public void Non_boolean_result_fails_closed()
        => _gate.IsSatisfied("invoice.erpCompany", new Snapshot(new InvoiceSnap("2000", "1DS", "X"))).Should().BeFalse();
}
