using System.Linq.Expressions;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.CandidateFilters;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R9 (D-R9-15) — startup reflection discovers the code-registered candidate filters; unknown names are
/// save-blocked; StatusIn params are dry-built at validation so bad params fail at save, not at scan.
/// </summary>
public class CandidateFilterRegistryTests
{
    private readonly CandidateFilterRegistry _registry = new();

    [Fact]
    public void Discovers_all_six_seed_filters()
    {
        var names = _registry.All.Select(f => (f.PortalEntity, f.Name)).ToList();
        names.Should().Contain((LnPortalEntity.Invoice, "InvoiceSubmittedUnposted"));
        names.Should().Contain((LnPortalEntity.Asn, "AsnSubmitted"));
        names.Should().Contain((LnPortalEntity.PurchaseOrder, "StatusIn"));
        names.Should().Contain((LnPortalEntity.SupplierChange, "SupplierChangeApproved"));
        names.Should().Contain((LnPortalEntity.Supplier, "SupplierRegistrationApprovedNoErpCode"));
        names.Should().Contain((LnPortalEntity.PoNegotiation, "PoNegotiationApproved"));
    }

    [Fact]
    public void Unknown_name_is_save_blocked()
    {
        _registry.TryValidate(LnPortalEntity.Invoice, "TotallyMadeUp", null, out var error).Should().BeFalse();
        error.Should().Contain("code-registered");
    }

    [Fact]
    public void StatusIn_params_matrix()
    {
        _registry.TryValidate(LnPortalEntity.PurchaseOrder, "StatusIn", "{\"statuses\":[\"Accepted\"]}", out var ok).Should().BeTrue(ok);
        _registry.TryValidate(LnPortalEntity.PurchaseOrder, "StatusIn", null, out var missing).Should().BeFalse();
        missing.Should().Contain("requires params");
        _registry.TryValidate(LnPortalEntity.PurchaseOrder, "StatusIn", "{\"statuses\":[\"NotAStatus\"]}", out var bad).Should().BeFalse();
        bad.Should().Contain("NotAStatus");
        _registry.TryValidate(LnPortalEntity.PurchaseOrder, "StatusIn", "{\"statuses\":[]}", out var empty).Should().BeFalse();
        empty.Should().Contain("at least one");
    }

    [Fact]
    public void Parameterless_filter_rejects_params()
    {
        _registry.TryValidate(LnPortalEntity.Asn, "AsnSubmitted", "{\"x\":1}", out var error).Should().BeFalse();
        error.Should().Contain("takes no params");
    }

    [Fact]
    public void Resolve_yields_a_working_ef_predicate()
    {
        var lambda = _registry.Resolve(LnPortalEntity.PurchaseOrder, "StatusIn", "{\"statuses\":[\"Accepted\",\"Rejected\"]}");
        var predicate = ((Expression<Func<PurchaseOrder, bool>>)lambda).Compile();
        predicate(new PurchaseOrder { PoStatus = PoStatus.Accepted }).Should().BeTrue();
        predicate(new PurchaseOrder { PoStatus = PoStatus.Rejected }).Should().BeTrue();
        predicate(new PurchaseOrder { PoStatus = PoStatus.Released }).Should().BeFalse();
        predicate(new PurchaseOrder { PoStatus = PoStatus.Accepted, IsDeleted = true }).Should().BeFalse();
    }
}
