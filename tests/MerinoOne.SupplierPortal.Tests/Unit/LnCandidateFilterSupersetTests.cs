using System.Linq.Expressions;
using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Integration.CandidateFilters;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Xunit;
using SupplierEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.Supplier;
using SupplierChangeRequestEntity = MerinoOne.SupplierPortal.Domain.Entities.Supplier.SupplierChangeRequest;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// R9 PHASE B ENTRY GATE (TSD §2.5a) — the SUPERSET guarantee, execution-reconfirmed: for every seed
/// candidate filter, an entity in each gate-ELIGIBLE state must match the filter (over-inclusive is fine —
/// the gate rejects extras; under-inclusive means rows the sweep never sees). Also pins that every
/// combination the LnOutboundSeeder writes resolves in the registry with its params.
/// </summary>
public class LnCandidateFilterSupersetTests
{
    private readonly CandidateFilterRegistry _registry = new();

    private Func<T, bool> Compile<T>(string portalEntity, string name, string? paramsJson = null)
        => ((Expression<Func<T, bool>>)_registry.Resolve(portalEntity, name, paramsJson)).Compile();

    [Fact]
    public void Invoice_filter_covers_both_auto_post_claim_states()
    {
        // The GRN auto-post claim (UpsertGoodsReceiptStatusCommand guard (c)) accepts Submitted OR Matched —
        // the TSD's original Submitted-only seed was UNDER-inclusive (V2.2 changelog #5).
        var filter = Compile<Invoice>(LnPortalEntity.Invoice, "InvoiceSubmittedUnposted");
        filter(new Invoice { InvoiceStatus = InvoiceStatus.Submitted, ErpPostedAt = null }).Should().BeTrue();
        filter(new Invoice { InvoiceStatus = InvoiceStatus.Matched, ErpPostedAt = null }).Should().BeTrue();
        // Posted / deleted / draft rows may be excluded — the cheap posted-marker exclusion is the filter's point.
        filter(new Invoice { InvoiceStatus = InvoiceStatus.Submitted, ErpPostedAt = DateTime.UtcNow }).Should().BeFalse();
        filter(new Invoice { InvoiceStatus = InvoiceStatus.Draft, ErpPostedAt = null }).Should().BeFalse();
        filter(new Invoice { InvoiceStatus = InvoiceStatus.Submitted, IsDeleted = true }).Should().BeFalse();
    }

    [Fact]
    public void Asn_filter_covers_the_submitted_post_state()
    {
        var filter = Compile<Asn>(LnPortalEntity.Asn, "AsnSubmitted");
        filter(new Asn { AsnStatus = AsnStatus.Submitted }).Should().BeTrue();
        filter(new Asn { AsnStatus = AsnStatus.Draft }).Should().BeFalse();
        filter(new Asn { AsnStatus = AsnStatus.Submitted, IsDeleted = true }).Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"statuses\":[\"Acknowledged\"]}", PoStatus.Acknowledged)]
    [InlineData("{\"statuses\":[\"Accepted\"]}", PoStatus.Accepted)]
    [InlineData("{\"statuses\":[\"Rejected\"]}", PoStatus.Rejected)]
    public void Po_StatusIn_covers_each_response_state_with_its_seeded_params(string paramsJson, PoStatus eligible)
    {
        var filter = Compile<PurchaseOrder>(LnPortalEntity.PurchaseOrder, "StatusIn", paramsJson);
        filter(new PurchaseOrder { PoStatus = eligible }).Should().BeTrue();
        filter(new PurchaseOrder { PoStatus = PoStatus.Released }).Should().BeFalse();
    }

    [Fact]
    public void SupplierChange_filter_covers_the_approved_push_state()
    {
        var filter = Compile<SupplierChangeRequestEntity>(LnPortalEntity.SupplierChange, "SupplierChangeApproved");
        filter(new SupplierChangeRequestEntity { ChangeStatus = ChangeRequestStatus.Approved }).Should().BeTrue();
        // Over-inclusion of Pushed rows is handled by deterministic-key exclusion, NOT required here —
        // but Approved (the enqueue state) MUST match.
        filter(new SupplierChangeRequestEntity { ChangeStatus = ChangeRequestStatus.Draft }).Should().BeFalse();
    }

    [Fact]
    public void Supplier_filter_covers_approved_onboarding_without_erp_code()
    {
        // Execution-reconfirmed field names: RegistrationStatus (no OnboardingStatus exists) + ErpCode.
        var filter = Compile<SupplierEntity>(LnPortalEntity.Supplier, "SupplierRegistrationApprovedNoErpCode");
        filter(new SupplierEntity { RegistrationStatus = RegistrationStatus.Approved, ErpCode = null }).Should().BeTrue();
        filter(new SupplierEntity { RegistrationStatus = RegistrationStatus.Approved, ErpCode = "S-100" }).Should().BeFalse();
        filter(new SupplierEntity { RegistrationStatus = RegistrationStatus.Registering, ErpCode = null }).Should().BeFalse();
    }

    [Fact]
    public void PoNegotiation_filter_covers_the_approved_push_state()
    {
        var filter = Compile<PurchaseOrderNegotiation>(LnPortalEntity.PoNegotiation, "PoNegotiationApproved");
        filter(new PurchaseOrderNegotiation { NegotiationStatus = PoNegotiationStatus.Approved }).Should().BeTrue();
        filter(new PurchaseOrderNegotiation { NegotiationStatus = PoNegotiationStatus.Submitted }).Should().BeFalse();
    }

    [Fact]
    public void Every_seeded_filter_name_and_params_resolves_in_the_registry()
    {
        // Mirrors LnOutboundSeeder.CandidateFilterByType — a seeder/registry drift here means config rows
        // that can never scan in Phase B.
        var seeded = new (string PortalEntity, string Name, string? ParamsJson)[]
        {
            (LnPortalEntity.Invoice, "InvoiceSubmittedUnposted", null),
            (LnPortalEntity.Asn, "AsnSubmitted", null),
            (LnPortalEntity.PurchaseOrder, "StatusIn", "{\"statuses\":[\"Acknowledged\"]}"),
            (LnPortalEntity.PurchaseOrder, "StatusIn", "{\"statuses\":[\"Accepted\"]}"),
            (LnPortalEntity.PurchaseOrder, "StatusIn", "{\"statuses\":[\"Rejected\"]}"),
            (LnPortalEntity.SupplierChange, "SupplierChangeApproved", null),
            (LnPortalEntity.Supplier, "SupplierRegistrationApprovedNoErpCode", null),
            (LnPortalEntity.PoNegotiation, "PoNegotiationApproved", null),
        };
        foreach (var (entity, name, paramsJson) in seeded)
        {
            _registry.TryValidate(entity, name, paramsJson, out var error)
                .Should().BeTrue($"seeded filter ({entity}, {name}) must validate: {error}");
            _registry.Resolve(entity, name, paramsJson).Should().NotBeNull();
        }
    }
}
