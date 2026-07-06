using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R9 — the Live-path behaviours of <see cref="LnDynamicDispatcher"/> driven directly with a stub
/// transport (real DB, real builders, real engine, Integration:Mode=Live via in-memory config):
/// 4xx permanent / 5xx retriable classification, contract-invalid-after-landed-2xx posture, and the
/// D-R9-20 erpStatus write to <c>PurchaseOrder.ErpStatus</c> (never <c>PoStatus</c>).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LnDynamicFailureTests
{
    private sealed class StubTransport : ILnHttpTransport
    {
        public LnHttpOutcome Next { get; set; } = new(null, null, "not configured");
        public Task<LnHttpOutcome> SendAsync(Guid tenantId, string httpVerb, string relativePath, string bodyJson, string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(Next);
    }

    private readonly IntegrationTestFixture _fx;
    public LnDynamicFailureTests(IntegrationTestFixture fx) => _fx = fx;

    private static readonly LnDefaultExpressions Defaults = new();

    private (LnDynamicDispatcher Dispatcher, StubTransport Transport, IServiceScope Scope) Build()
    {
        var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var transport = new StubTransport();
        var liveCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Integration:Mode"] = "Live" })
            .Build();
        var dispatcher = new LnDynamicDispatcher(
            db,
            scope.ServiceProvider.GetRequiredService<ILnInputDocumentBuilderRegistry>(),
            scope.ServiceProvider.GetRequiredService<ILnMappingService>(),
            transport,
            Defaults,
            liveCfg,
            NullLogger<LnDynamicDispatcher>.Instance);
        return (dispatcher, transport, scope);
    }

    private async Task<(OutboxMessage Row, LnEndpointRoute Route, Guid PoId)> SeedPoAcceptAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid(), PoNumber = $"PO-DYN-{tag}", SupplierId = IntegrationTestFixture.SupplierId,
            PoType = PoType.Material, PoDate = now.Date, PoStatus = PoStatus.Accepted,
            AcceptedAt = now, SeccodeId = IntegrationTestFixture.SeccodeId,
            TenantId = IntegrationTestFixture.TenantId, TenantEntityId = IntegrationTestFixture.CompanyId,
            CreatedBy = "seed", CreatedOn = now,
        };
        db.PurchaseOrders.Add(po);
        await db.SaveChangesAsync();

        var row = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = IntegrationTestFixture.TenantId,
            TransactionType = OutboxTransactionType.PoAccept,
            EntityName = OutboxEntity.PurchaseOrder,
            EntityId = po.Id,
            DeterministicKey = OutboxKey.For(OutboxEntity.PurchaseOrder, IntegrationTestFixture.TenantId, po.PoNumber, "accept"),
            Status = OutboxStatus.Sending,
            CreatedBy = "seed",
            CreatedOn = now,
        };
        var entry = Defaults.TryGet(OutboxTransactionType.PoAccept)!;
        var route = new LnEndpointRoute(
            IntegrationTestFixture.TenantId, OutboxTransactionType.PoAccept, LnDispatchMode.Dynamic,
            LnPortalEntity.PurchaseOrder, "LN/lnapi/odata/tdapi.purchaseOrders/Acceptances", "POST",
            entry.RequestExpr, entry.ResponseExpr);
        return (row, route, po.Id);
    }

    [SkippableFact]
    public async Task Http_422_is_permanent_with_enriched_odata_error_text()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (row, route, _) = await SeedPoAcceptAsync();
        var (dispatcher, transport, scope) = Build();
        using (scope)
        {
            transport.Next = new LnHttpOutcome(422, "{\"error\":{\"message\":{\"value\":\"Order does not exist\"}}}", null);
            var outcome = await dispatcher.DispatchAsync(row, route);
            outcome.PermanentFailure.Should().BeTrue();
            outcome.Result.Success.Should().BeFalse();
            outcome.Result.Message.Should().Contain("HTTP 422").And.Contain("Order does not exist");
        }
    }

    [SkippableFact]
    public async Task Http_503_is_retriable()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (row, route, _) = await SeedPoAcceptAsync();
        var (dispatcher, transport, scope) = Build();
        using (scope)
        {
            transport.Next = new LnHttpOutcome(503, "busy", null);
            var outcome = await dispatcher.DispatchAsync(row, route);
            outcome.PermanentFailure.Should().BeFalse();
            outcome.Result.Success.Should().BeFalse();
        }
    }

    [SkippableFact]
    public async Task Landed_201_extracts_erpKey_and_stamps_only_the_erp_owned_status_column()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (row, route, poId) = await SeedPoAcceptAsync();
        var (dispatcher, transport, scope) = Build();
        using (scope)
        {
            transport.Next = new LnHttpOutcome(201, "{\"id\":\"LN-ACC-777\"}", null);
            var outcome = await dispatcher.DispatchAsync(row, route);
            outcome.Result.Success.Should().BeTrue(outcome.Result.Message);
            outcome.Result.ErpCode.Should().Be("LN-ACC-777"); // → worker sync-ack seam flips the row Acked
        }

        using var verify = _fx.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == poId);
        // D-R9-20: erpStatus lands in the EXISTING ERP-owned column; the portal workflow status is untouched.
        po.ErpStatus.Should().Be("Created");
        po.PoStatus.Should().Be(PoStatus.Accepted);
    }

    [SkippableFact]
    public async Task Contract_invalid_output_after_landed_2xx_is_retriable_with_verify_warning()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var (row, route, _) = await SeedPoAcceptAsync();
        // Break the response mapping so it emits an unknown key against the contract.
        route = route with { ResponseMappingExpr = "{ \"erpKey\": $string(id), \"erpStatus\": \"Created\", \"rogue\": true }" };
        var (dispatcher, transport, scope) = Build();
        using (scope)
        {
            transport.Next = new LnHttpOutcome(201, "{\"id\":\"LN-X\"}", null);
            var outcome = await dispatcher.DispatchAsync(row, route);
            outcome.PermanentFailure.Should().BeFalse();       // POST landed — LN dedupes the replayed key on re-arm
            outcome.Result.Success.Should().BeFalse();
            outcome.Result.Message.Should().Contain("VERIFY IN LN BEFORE RE-ARM").And.Contain("'rogue'");
        }
    }
}
