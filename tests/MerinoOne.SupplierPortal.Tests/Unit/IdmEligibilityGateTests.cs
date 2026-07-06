using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using MerinoOne.SupplierPortal.Infrastructure.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R8 §4.3 semantics, re-pinned 1:1 on the R9 shared JSONata gate (TSD R9 §2.11, D-R9-16): every legacy
/// dot-path array converts via <see cref="IdmGateConversion"/> to a null-safe conjunction whose verdicts
/// MATCH the old JsonPath gate across the whole matrix — all-present true; any-null false; whitespace
/// false; missing segment false; empty/malformed fails closed. This is the old-vs-new equivalence proof
/// (the C# converter mirrors the 0049 migration's SQL).
/// </summary>
public class IdmEligibilityGateTests
{
    private readonly JsonataEligibilityGate _gate = new(new LnMappingService());

    private static object Snapshot(string? company, string? txnType, string? docNo) => new Dictionary<string, object?>
    {
        ["invoice"] = new Dictionary<string, object?>
        {
            ["erpCompany"] = company,
            ["erpTransactionType"] = txnType,
            ["erpDocumentNo"] = docNo,
        },
    };

    private const string LegacyInvoiceGate = "[\"invoice.erpCompany\",\"invoice.erpTransactionType\",\"invoice.erpDocumentNo\"]";
    private static readonly string InvoiceGate = IdmGateConversion.ConvertStoredValue(LegacyInvoiceGate);

    [Fact]
    public void Conversion_renders_the_null_safe_conjunction()
        => InvoiceGate.Should().Be(
            "(invoice.erpCompany != null and $trim($string(invoice.erpCompany)) != \"\") and " +
            "(invoice.erpTransactionType != null and $trim($string(invoice.erpTransactionType)) != \"\") and " +
            "(invoice.erpDocumentNo != null and $trim($string(invoice.erpDocumentNo)) != \"\")");

    [Fact]
    public void All_paths_present_satisfies()
        => _gate.IsSatisfied(InvoiceGate, Snapshot("2000", "1DS", "23063669")).Should().BeTrue();

    [Fact]
    public void Any_null_path_fails()
    {
        _gate.IsSatisfied(InvoiceGate, Snapshot(null, "1DS", "23063669")).Should().BeFalse();
        _gate.IsSatisfied(InvoiceGate, Snapshot("2000", null, "23063669")).Should().BeFalse();
        _gate.IsSatisfied(InvoiceGate, Snapshot("2000", "1DS", null)).Should().BeFalse();
    }

    [Fact]
    public void Empty_or_whitespace_path_fails()
        => _gate.IsSatisfied(InvoiceGate, Snapshot("2000", "  ", "23063669")).Should().BeFalse();

    [Fact]
    public void Missing_path_segment_fails()
        => _gate.IsSatisfied(IdmGateConversion.ConvertStoredValue("[\"invoice.doesNotExist\"]"), Snapshot("2000", "1DS", "9"))
            .Should().BeFalse();

    [Fact]
    public void Empty_or_malformed_gate_fails_closed()
    {
        // Empty array converts to a blank expression → never satisfied, exactly like the old gate.
        _gate.IsSatisfied(IdmGateConversion.ConvertStoredValue("[]"), Snapshot("2000", "1DS", "9")).Should().BeFalse();
        // Malformed legacy JSON passes through unchanged and fails to compile → fail closed.
        _gate.IsSatisfied(IdmGateConversion.ConvertStoredValue("not json ["), Snapshot("2000", "1DS", "9")).Should().BeFalse();
    }

    [Fact]
    public void An_already_converted_expression_passes_through_unchanged()
        => IdmGateConversion.ConvertStoredValue(InvoiceGate).Should().Be(InvoiceGate);
}
