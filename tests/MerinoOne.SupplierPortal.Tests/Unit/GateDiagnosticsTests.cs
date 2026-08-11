using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using MerinoOne.SupplierPortal.Infrastructure.Integration;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// 2026-08-11 — a Blocked IDM outbox row used to carry no reason at all. These pin the sentence the dispatcher
/// now writes to <c>lastError</c>: it must NAME the failing gate term, not just restate that the gate failed.
/// </summary>
public class GateDiagnosticsTests
{
    private readonly IEligibilityGate _gate = new JsonataEligibilityGate(new LnMappingService());

    private static readonly string AsnGate = IdmGateConversion.ToJsonata(new[] { "asn.erpCode", "asn.supplier.erpCode" });

    private static Dictionary<string, object?> Snapshot(string? erpCode, string? supplierErpCode) => new()
    {
        ["asn"] = new Dictionary<string, object?>
        {
            ["erpCode"] = erpCode,
            ["supplier"] = new Dictionary<string, object?> { ["erpCode"] = supplierErpCode },
        },
    };

    [Fact]
    public void Names_only_the_failing_term()
    {
        var reason = GateDiagnostics.Describe(_gate, AsnGate, Snapshot("INB00107", null));

        reason.Should().Contain("asn.supplier.erpCode");
        reason.Should().NotContain("missing or empty: asn.erpCode");   // the satisfied term is not accused
    }

    [Fact]
    public void Names_every_failing_term()
    {
        var reason = GateDiagnostics.Describe(_gate, AsnGate, Snapshot(null, "   "));

        reason.Should().Contain("asn.erpCode").And.Contain("asn.supplier.erpCode");
    }

    [Fact]
    public void Reports_a_term_whose_path_is_absent_from_the_snapshot_entirely()
    {
        // The real R16.1 case: old binaries emit no asn.supplier block at all, so the path is undefined
        // (not null) — the strict-true engine fails closed and the term must still be named.
        var noSupplierBlock = new Dictionary<string, object?>
        {
            ["asn"] = new Dictionary<string, object?> { ["erpCode"] = "INB00113" },
        };

        GateDiagnostics.Describe(_gate, AsnGate, noSupplierBlock).Should().Contain("asn.supplier.erpCode");
    }

    [Fact]
    public void Falls_back_to_the_whole_expression_when_the_gate_is_not_a_plain_conjunction()
    {
        // `and` binds tighter than `or`, so lifting a conjunct out of a disjunction would accuse an innocent
        // term. The describer must decline to decompose and quote the gate instead.
        const string expr = "(asn.erpCode != null) or (asn.supplier.erpCode != null and asn.asnNumber != null)";

        var reason = GateDiagnostics.Describe(_gate, expr, Snapshot(null, null));

        reason.Should().NotContain("missing or empty");
        reason.Should().Contain("asn.erpCode != null) or (");
    }

    [Fact]
    public void Single_term_gate_quotes_itself()
    {
        var single = IdmGateConversion.ToJsonata(new[] { "asn.erpCode" });

        var reason = GateDiagnostics.Describe(_gate, single, Snapshot(null, null));

        reason.Should().Contain("asn.erpCode");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_gate_never_claims_a_term(string? blank)
    {
        GateDiagnostics.Describe(_gate, blank, Snapshot(null, null)).Should().Be("Eligibility gate not satisfied.");
    }

    [Fact]
    public void Does_not_split_on_and_inside_a_string_literal()
    {
        // A literal containing " and " must not be mistaken for a conjunction boundary.
        const string expr = "(asn.status != \"packed and sealed\") and (asn.erpCode != null)";

        GateDiagnostics.SplitTopLevelConjuncts(expr).Should().HaveCount(2);
    }
}
