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

        /// <summary>R12 — null until something is actually sent, so a test can assert nothing reached LN.</summary>
        public string? LastBody { get; private set; }

        public Task<LnHttpOutcome> SendAsync(Guid tenantId, string httpVerb, string relativePath, string bodyJson, string idempotencyKey, string? documentCompany = null, CancellationToken ct = default)
        {
            LastBody = bodyJson;
            return Task.FromResult(Next);
        }
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
            IntegrationTestFixture.TenantId, OutboxTransactionType.PoAccept, OutboundDispatchMode.Dynamic,
            LnPortalEntity.PurchaseOrder, "CustomerApi/LNAPI/PO_Update", "POST",
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
            // R12 — the REAL LN PO_Update envelope, not an OData created-entity. erpKey is Header.OrderNo:
            // a PO response creates no new ERP document, so the order number IS the handle.
            transport.Next = new LnHttpOutcome(201, """
                { "PurchaseOrder": [ { "Header": { "OrderNo": "LN-ACC-777", "Status": "Success", "Remarks": "" },
                                       "Line": [ { "LineNo": "10", "SeqNo": "1", "Status": "Success", "Remarks": "" } ] } ] }
                """, null);
            var outcome = await dispatcher.DispatchAsync(row, route);
            outcome.Result.Success.Should().BeTrue(outcome.Result.Message);
            outcome.Result.ErpCode.Should().Be("LN-ACC-777"); // → worker sync-ack seam flips the row Acked
        }

        using var verify = _fx.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == poId);
        // D-R9-20: erpStatus lands in the EXISTING ERP-owned column; the portal workflow status is untouched.
        po.ErpStatus.Should().Be("Success");
        po.PoStatus.Should().Be(PoStatus.Accepted);
    }

    [SkippableFact]
    public async Task Landed_200_with_a_non_success_header_status_fails_retriably()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // R12 (D12) — LN answers HTTP 200 with a LOGICAL verdict in Header.Status. A non-Success verdict is a
        // failed post that merely arrived intact: it must NOT Ack the row, and it must not stamp erpStatus.
        // Retriable, because the deterministic key is replayed verbatim and LN dedupes a genuine duplicate.
        var (row, route, poId) = await SeedPoAcceptAsync();
        var (dispatcher, transport, scope) = Build();
        using (scope)
        {
            transport.Next = new LnHttpOutcome(200, """
                { "PurchaseOrder": [ { "Header": { "OrderNo": "PO-X", "Status": "Error", "Remarks": "order is closed" },
                                       "Line": [] } ] }
                """, null);
            var outcome = await dispatcher.DispatchAsync(row, route);

            outcome.Result.Success.Should().BeFalse("a logical rejection is a failed post, not a slow success");
            outcome.PermanentFailure.Should().BeFalse("the key replays, so LN dedupes if the retry is a duplicate");
            outcome.Result.Message.Should().Contain("Error").And.Contain("order is closed");
            outcome.Result.ErpCode.Should().BeNull("nothing was applied, so there is no ERP handle to record");
        }

        using var verify = _fx.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var po = await db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == poId);
        po.ErpStatus.Should().BeNull("a rejected post must not stamp the ERP-owned status column");
    }

    [SkippableFact]
    public async Task A_po_with_no_company_fails_permanently_without_calling_ln()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // R12 (D9) — PO_Update routes on the BODY's CompanyCode (D21), so unlike the OData endpoints there is
        // no X-Infor-LnCompany fallback to rescue a PO with no company. R11.3 established that a wrong company
        // is a wrong-company WRITE in LN, so a missing one stops the dispatch outright.
        var (row, route, poId) = await SeedPoAcceptAsync();
        using (var seed = _fx.Factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PurchaseOrders.IgnoreQueryFilters().Where(p => p.Id == poId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.TenantEntityId, (Guid?)null));
        }

        var (dispatcher, transport, scope) = Build();
        using (scope)
        {
            transport.Next = new LnHttpOutcome(201, "{}", null);
            var outcome = await dispatcher.DispatchAsync(row, route);

            outcome.Result.Success.Should().BeFalse();
            outcome.PermanentFailure.Should().BeTrue("retrying cannot invent a company — a human must fix the PO");
            outcome.Result.Message.Should().Contain("company");
            transport.LastBody.Should().BeNull("nothing may reach LN without a company");
        }
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
