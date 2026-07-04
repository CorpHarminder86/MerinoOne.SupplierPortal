using FluentAssertions;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>R8 — TSD R8 §4.3. The config-driven eligibility gate: every dot-path must resolve to a non-empty value.</summary>
public class IdmEligibilityGateTests
{
    private readonly JsonPathEligibilityGate _gate = new();

    private static object Snapshot(string? company, string? txnType, string? docNo) => new Dictionary<string, object?>
    {
        ["invoice"] = new Dictionary<string, object?>
        {
            ["erpCompany"] = company,
            ["erpTransactionType"] = txnType,
            ["erpDocumentNo"] = docNo,
        },
    };

    private const string InvoiceGate = "[\"invoice.erpCompany\",\"invoice.erpTransactionType\",\"invoice.erpDocumentNo\"]";

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
        => _gate.IsSatisfied("[\"invoice.doesNotExist\"]", Snapshot("2000", "1DS", "9")).Should().BeFalse();

    [Fact]
    public void Empty_or_malformed_gate_fails_closed()
    {
        _gate.IsSatisfied("[]", Snapshot("2000", "1DS", "9")).Should().BeFalse();
        _gate.IsSatisfied("not json", Snapshot("2000", "1DS", "9")).Should().BeFalse();
    }
}
