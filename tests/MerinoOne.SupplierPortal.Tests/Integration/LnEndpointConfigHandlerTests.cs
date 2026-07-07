using FluentAssertions;
using FluentValidation;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.CandidateFilters;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Application.Integration.Ln.Commands;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R9 — the config admin surface end-to-end at the handler level (isolated synthetic tenant so the
/// seeded fixture-tenant rows stay pristine): save-time blocking (bad JSONata / unknown filter /
/// closed-contract violation), gateVersion bumping, D-R9-18 sample pinning, and the D-R9-17/21
/// attestation + pathConfirmed gates on → Dynamic.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class OutboundIntegrationConfigHandlerTests
{
    /// <summary>
    /// Synthetic tenant, FRESH PER TEST (xUnit news the class per test method) — OutboundIntegrationConfig carries
    /// no Tenant FK, so each test's upsert-by-(tenant, transactionType) state is fully isolated from other
    /// tests AND from prior runs against the shared test DB (a fixed guid poisoned gateVersion assertions).
    /// </summary>
    private readonly Guid _tenantId = Guid.NewGuid();

    private sealed class StubUser : ICurrentUser
    {
        private readonly Guid _tenant;
        public StubUser(Guid tenant) => _tenant = tenant;
        public string UserCode => "test-admin";
        public string? UserName => "Test Admin";
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public IReadOnlyCollection<string> Permissions => Array.Empty<string>();
        public bool IsAuthenticated => true;
        public bool IsManager => false;
        public bool IsAdmin => true;
        public bool HasPermission(string code) => true;
        public Guid? TenantId => _tenant;
        public bool IsPlatformAdmin => false;
    }

    private readonly IntegrationTestFixture _fx;
    public OutboundIntegrationConfigHandlerTests(IntegrationTestFixture fx) => _fx = fx;

    private (IServiceScope Scope, AppDbContext Db, StubUser User,
             ILnMappingService Mapping, ILnInputDocumentBuilderRegistry Builders,
             ICandidateFilterRegistry Filters, ILnExpressionCatalog Catalog) Services()
    {
        var scope = _fx.Factory.Services.CreateScope();
        return (scope,
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            new StubUser(_tenantId),
            scope.ServiceProvider.GetRequiredService<ILnMappingService>(),
            scope.ServiceProvider.GetRequiredService<ILnInputDocumentBuilderRegistry>(),
            scope.ServiceProvider.GetRequiredService<ICandidateFilterRegistry>(),
            scope.ServiceProvider.GetRequiredService<ILnExpressionCatalog>());
    }

    private static SaveOutboundIntegrationConfigRequest ValidInvoiceRequest(ILnExpressionCatalog catalog, Guid? id = null)
    {
        var repo = catalog.TryGet(OutboxTransactionType.InvoicePost)!;
        return new SaveOutboundIntegrationConfigRequest(
            Id: id,
            Kind: "Transaction",
            ConnectionPointId: null,
            TransactionType: OutboxTransactionType.InvoicePost,
            PortalEntity: LnPortalEntity.Invoice,
            AttachmentType: null,
            TargetEntityName: null,
            ContextJson: null,
            EndpointPath: "LN/lnapi/odata/cisli.selfBillingInvoices/Invoices",
            HttpVerb: "POST",
            MutatePath: null, MutateVerb: null, DeletePath: null, DeleteVerb: null,
            StaticHeadersJson: null,
            RequestFormat: null, ResponseFormat: null,
            EligibilityGateExpr: null,
            RequestMappingExpr: repo.RequestExpr,
            MutateMappingExpr: null,
            ResponseMappingExpr: repo.ResponseExpr,
            AckMappingExpr: repo.AckExpr,
            CandidateFilterName: "InvoiceSubmittedUnposted",
            CandidateFilterParams: null,
            ResponseSampleJson: catalog.ODataCreatedEntitySample,
            AckSampleJson: catalog.ErpAckBodySample);
    }

    private async Task<Guid> SaveValidAsync()
    {
        var (scope, db, user, mapping, builders, filters, catalog) = Services();
        using (scope)
        {
            var handler = new SaveOutboundIntegrationConfigCommandHandler(db, user, mapping, builders, filters);
            return await handler.Handle(new SaveOutboundIntegrationConfigCommand(ValidInvoiceRequest(catalog)), CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Save_blocks_bad_jsonata_unknown_filter_and_contract_violation()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (scope, db, user, mapping, builders, filters, catalog) = Services();
        using (scope)
        {
            var handler = new SaveOutboundIntegrationConfigCommandHandler(db, user, mapping, builders, filters);
            var valid = ValidInvoiceRequest(catalog);

            var badJsonata = valid with { RequestMappingExpr = "{{{{ not jsonata" };
            await Assert.ThrowsAsync<ValidationException>(() =>
                handler.Handle(new SaveOutboundIntegrationConfigCommand(badJsonata), CancellationToken.None));

            var unknownFilter = valid with { CandidateFilterName = "NoSuchFilter" };
            var ex2 = await Assert.ThrowsAsync<ValidationException>(() =>
                handler.Handle(new SaveOutboundIntegrationConfigCommand(unknownFilter), CancellationToken.None));
            ex2.Message.Should().Contain("code-registered");

            // Response mapping emits an unknown key against the response sample → CLOSED contract blocks save.
            var rogueResponse = valid with { ResponseMappingExpr = "{ \"erpKey\": $string(id), \"erpStatus\": \"Created\", \"rogue\": 1 }" };
            var ex3 = await Assert.ThrowsAsync<ValidationException>(() =>
                handler.Handle(new SaveOutboundIntegrationConfigCommand(rogueResponse), CancellationToken.None));
            ex3.Message.Should().Contain("'rogue'");
        }
    }

    [SkippableFact]
    public async Task Save_bumps_gateVersion_only_on_gate_or_mapping_change()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var id = await SaveValidAsync();

        // No-change re-save: gateVersion stays.
        var (s1, db1, u1, m1, b1, f1, cat1) = Services();
        using (s1)
        {
            var handler = new SaveOutboundIntegrationConfigCommandHandler(db1, u1, m1, b1, f1);
            await handler.Handle(new SaveOutboundIntegrationConfigCommand(ValidInvoiceRequest(cat1, id)), CancellationToken.None);
            var row = await db1.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == id);
            row.GateVersion.Should().Be(1);
            row.DispatchMode.Should().Be(OutboundDispatchMode.Legacy, "creation/save never changes dispatch mode");
        }

        // Gate-expression change: bumps.
        var (s2, db2, u2, m2, b2, f2, cat2) = Services();
        using (s2)
        {
            var handler = new SaveOutboundIntegrationConfigCommandHandler(db2, u2, m2, b2, f2);
            var withGate = ValidInvoiceRequest(cat2, id) with { EligibilityGateExpr = "invoiceStatus = \"Submitted\"" };
            await handler.Handle(new SaveOutboundIntegrationConfigCommand(withGate), CancellationToken.None);
            var row = await db2.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == id);
            row.GateVersion.Should().Be(2);
        }
    }

    [SkippableFact]
    public async Task Dynamic_gate_matrix_attestation_pathConfirmed_and_sample_all_required()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var id = await SaveValidAsync();

        // 1. Nothing recorded → blocked, all three blockers named.
        var (s1, db1, u1, m1, b1, f1, _) = Services();
        using (s1)
        {
            var handler = new SetOutboundDispatchModeCommandHandler(db1, u1, m1, b1, f1);
            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                handler.Handle(new SetOutboundDispatchModeCommand(id, "Dynamic"), CancellationToken.None));
            ex.Message.Should().Contain("attestation").And.Contain("Available-APIs").And.Contain("sample");
        }

        // 2. Attest WITHOUT the pathConfirmed checkbox (D-R9-21) → still blocked.
        var (s2, db2, u2, _, _, _, _) = Services();
        using (s2)
        {
            await new AttestLnEndpointCommandHandler(db2, u2)
                .Handle(new AttestLnEndpointCommand(id, new AttestLnEndpointRequest("dry-posted against test tenant", PathConfirmed: false)), CancellationToken.None);
        }
        var (s3, db3, u3, m3, b3, f3, _) = Services();
        using (s3)
        {
            var handler = new SetOutboundDispatchModeCommandHandler(db3, u3, m3, b3, f3);
            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                handler.Handle(new SetOutboundDispatchModeCommand(id, "Dynamic"), CancellationToken.None));
            ex.Message.Should().Contain("Available-APIs");
        }

        // 3. Tick the checkbox (note gains the stamped confirmation line) + pin a sample → Dynamic allowed.
        var (s4, db4, u4, _, _, _, _) = Services();
        using (s4)
        {
            await new AttestLnEndpointCommandHandler(db4, u4)
                .Handle(new AttestLnEndpointCommand(id, new AttestLnEndpointRequest("mock byte-parity verified", PathConfirmed: true)), CancellationToken.None);
            var row = await db4.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == id);
            row.PathConfirmed.Should().BeTrue();
            row.VerifiedBy.Should().Be("test-admin");
            row.VerifiedNote.Should().Contain(AttestLnEndpointCommandHandler.PathConfirmationLine);
        }
        var (s5, db5, u5, _, builders5, _, _) = Services();
        using (s5)
        {
            var pinned = await new PinLnSampleDocumentCommandHandler(db5, u5, builders5)
                .Handle(new PinLnSampleDocumentCommand(id, IntegrationTestFixture.InvoiceId), CancellationToken.None);
            pinned.Should().BeTrue();
            var row = await db5.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == id);
            row.SampleDocumentJson.Should().NotBeNullOrWhiteSpace();
            row.SampleBuilderVersion.Should().Be(LnInputDocumentVersions.Invoice);
        }
        var (s6, db6, u6, m6, b6, f6, _) = Services();
        using (s6)
        {
            var handler = new SetOutboundDispatchModeCommandHandler(db6, u6, m6, b6, f6);
            (await handler.Handle(new SetOutboundDispatchModeCommand(id, "Dynamic"), CancellationToken.None)).Should().BeTrue();
            var row = await db6.OutboundIntegrationConfigs.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == id);
            row.DispatchMode.Should().Be(OutboundDispatchMode.Dynamic);
        }

        // 4. → Held / → Legacy are never blocked (kill + rollback must always work).
        var (s7, db7, u7, m7, b7, f7, _) = Services();
        using (s7)
        {
            var handler = new SetOutboundDispatchModeCommandHandler(db7, u7, m7, b7, f7);
            (await handler.Handle(new SetOutboundDispatchModeCommand(id, "Held"), CancellationToken.None)).Should().BeTrue();
            (await handler.Handle(new SetOutboundDispatchModeCommand(id, "Legacy"), CancellationToken.None)).Should().BeTrue();
        }
    }
}
