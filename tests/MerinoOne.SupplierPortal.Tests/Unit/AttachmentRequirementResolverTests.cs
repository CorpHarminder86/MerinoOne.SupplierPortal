using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Domain.Enums;
using Xunit;
using static MerinoOne.SupplierPortal.Application.Common.Documents.AttachmentRequirementResolver;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R4 — TSD R4 Addendum §8.3 + D5 / UC-ATT-01..07. Pure, DB-less tests for the two-tier attachment-requirement
/// resolver. Proves: supplier override WINS over tenant default per (entity, type); Mandatory and Warning split
/// independently; Optional + present types are silent; Mandatory-before-Warning is a property of the two lists;
/// no policy rows → empty (never blocks).
/// </summary>
public class AttachmentRequirementResolverTests
{
    private static readonly Guid Supplier = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static PolicyRow Tenant(string code, AttachmentRequirement req)
        => new(code, code, null, req);
    private static PolicyRow Override(string code, AttachmentRequirement req)
        => new(code, code, Supplier, req);

    [Fact]
    public void NoPolicies_neverBlocks()
    {
        var ev = Resolve(Array.Empty<PolicyRow>(), Array.Empty<string>());
        ev.MissingMandatory.Should().BeEmpty();
        ev.MissingWarning.Should().BeEmpty();
    }

    [Fact]
    public void Mandatory_missing_is_listed()
    {
        var ev = Resolve(
            new[] { Tenant("TestCertificate", AttachmentRequirement.Mandatory) },
            Array.Empty<string>());
        ev.MissingMandatory.Should().ContainSingle().Which.Should().Be("TestCertificate");
        ev.MissingWarning.Should().BeEmpty();
    }

    [Fact]
    public void Mandatory_present_is_satisfied_caseInsensitive()
    {
        var ev = Resolve(
            new[] { Tenant("TestCertificate", AttachmentRequirement.Mandatory) },
            new[] { "testcertificate" });   // case-insensitive presence
        ev.MissingMandatory.Should().BeEmpty();
    }

    [Fact]
    public void Optional_missing_is_silent()
    {
        var ev = Resolve(
            new[] { Tenant("PackingSlip", AttachmentRequirement.Optional) },
            Array.Empty<string>());
        ev.MissingMandatory.Should().BeEmpty();
        ev.MissingWarning.Should().BeEmpty();
    }

    [Fact]
    public void Mandatory_and_Warning_split_independently()
    {
        // Invoice + TestCertificate Mandatory (both missing), PackingSlip Warning (missing) — UC-ATT-05.
        var ev = Resolve(
            new[]
            {
                Tenant("Invoice", AttachmentRequirement.Mandatory),
                Tenant("TestCertificate", AttachmentRequirement.Mandatory),
                Tenant("PackingSlip", AttachmentRequirement.Warning),
            },
            Array.Empty<string>());
        ev.MissingMandatory.Should().BeEquivalentTo(new[] { "Invoice", "TestCertificate" });
        ev.MissingWarning.Should().BeEquivalentTo(new[] { "PackingSlip" });
    }

    [Fact]
    public void SupplierOverride_wins_relaxing_to_Optional()
    {
        // Worked example: tenant ASN·PackingSlip=Warning; supplier override PackingSlip=Optional → supplier sees it
        // Optional (silent), even though present types are empty (supplier WINS — D5).
        var ev = Resolve(
            new[]
            {
                Tenant("PackingSlip", AttachmentRequirement.Warning),
                Override("PackingSlip", AttachmentRequirement.Optional),
            },
            Array.Empty<string>());
        ev.MissingWarning.Should().BeEmpty(because: "the supplier override relaxes PackingSlip to Optional");
        ev.MissingMandatory.Should().BeEmpty();
    }

    [Fact]
    public void SupplierOverride_wins_tightening_to_Mandatory()
    {
        // Tenant TestCertificate=Warning; supplier override TestCertificate=Mandatory → supplier sees it Mandatory.
        var ev = Resolve(
            new[]
            {
                Tenant("TestCertificate", AttachmentRequirement.Warning),
                Override("TestCertificate", AttachmentRequirement.Mandatory),
            },
            Array.Empty<string>());
        ev.MissingMandatory.Should().ContainSingle().Which.Should().Be("TestCertificate");
        ev.MissingWarning.Should().BeEmpty();
    }

    [Fact]
    public void TenantDefault_applies_when_no_override_for_that_type()
    {
        // Override exists for PackingSlip only; TestCertificate falls back to its tenant default (Mandatory).
        var ev = Resolve(
            new[]
            {
                Tenant("TestCertificate", AttachmentRequirement.Mandatory),
                Tenant("PackingSlip", AttachmentRequirement.Warning),
                Override("PackingSlip", AttachmentRequirement.Optional),
            },
            Array.Empty<string>());
        ev.MissingMandatory.Should().BeEquivalentTo(new[] { "TestCertificate" });
        ev.MissingWarning.Should().BeEmpty(because: "PackingSlip override relaxes it to Optional");
    }
}
