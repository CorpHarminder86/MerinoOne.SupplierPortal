using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R9 (D-R9-9, B4) — the dispatch-time gate re-check on the claimed row, THE final guard:
/// revoke-between-enqueue-and-dispatch lands the row <c>Skipped</c> (reason + gateVersion stamped) —
/// NOT Failed, NO IntegrationError, NO SyncLog, no LN call. Also: the global-outbound kill switch
/// holds dispatch while enqueue continues, and re-enabling drains (D-R9-11).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LnDispatchRecheckTests
{
    private readonly IntegrationTestFixture _fx;
    public LnDispatchRecheckTests(IntegrationTestFixture fx) => _fx = fx;

    private async Task DrainAsync()
    {
        var sf = _fx.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var cfg = _fx.Factory.Services.GetRequiredService<IConfiguration>();
        var worker = new OutboxDispatcherWorker(sf, NullLogger<OutboxDispatcherWorker>.Instance, cfg);
        await worker.DrainOnceAsync(CancellationToken.None);
    }

    private async Task SetInvoiceGateAsync(string gateExpr, int gateVersion = 11)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var defaults = new LnDefaultExpressions();
        var entry = defaults.TryGet(OutboxTransactionType.InvoicePost)!;
        var cfg = await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == IntegrationTestFixture.TenantId
                && c.TransactionType == OutboxTransactionType.InvoicePost && !c.IsDeleted);
        if (cfg is null)
        {
            cfg = new OutboundIntegrationConfig
            {
                TenantId = IntegrationTestFixture.TenantId,
                TransactionType = OutboxTransactionType.InvoicePost,
                PortalEntity = LnPortalEntity.Invoice,
                EndpointPath = "starter",
                RequestMappingExpr = entry.RequestExpr,
                ResponseMappingExpr = entry.ResponseExpr,
                CreatedBy = "seed",
            };
            db.OutboundIntegrationConfigs.Add(cfg);
        }
        cfg.DispatchMode = OutboundDispatchMode.Dynamic;
        cfg.EligibilityGateExpr = gateExpr;
        cfg.GateVersion = gateVersion;
        await db.SaveChangesAsync();
    }

    private async Task CleanupAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .Where(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.InvoicePost)
            .ExecuteDeleteAsync();
        await db.IntegrationSwitches.IgnoreQueryFilters()
            .Where(s => s.TenantId == IntegrationTestFixture.TenantId)
            .ExecuteDeleteAsync();
    }

    private async Task<Guid> EnqueuePendingAsync(string tag, int gateVersion = 10)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new OutboxMessage
        {
            Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
            TransactionType = OutboxTransactionType.InvoicePost, EntityName = OutboxEntity.Invoice,
            EntityId = IntegrationTestFixture.InvoiceId,
            DeterministicKey = OutboxKey.For(OutboxEntity.Invoice, IntegrationTestFixture.TenantId, $"recheck-{tag}", "post"),
            Status = OutboxStatus.Pending, GateVersion = gateVersion, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        };
        db.OutboxMessages.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [SkippableFact]
    public async Task Revoke_between_enqueue_and_dispatch_lands_Skipped_not_Failed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // The row was enqueued while eligible; by dispatch time the gate says no (the fixture invoice is
        // Submitted, the gate demands a status it does not have — the revoke-to-Draft shape).
        //
        // R12 (D17) — the row and the config now carry the SAME gateVersion, which is what a genuine revoke
        // looks like: the ENTITY changed, the gate did not. Before R12 this test enqueued at v10 against a
        // config at v11, which is a different scenario entirely (the gate itself changed after enqueue) and
        // is now deliberately exempt — see Gate_change_after_enqueue_does_not_retro_skip_the_row below.
        await SetInvoiceGateAsync("invoiceStatus = \"UnderReview\"", gateVersion: 11);
        var rowId = await EnqueuePendingAsync(Guid.NewGuid().ToString("N")[..8], gateVersion: 11);

        int errorsBefore, logsBefore;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            errorsBefore = await db.IntegrationErrors.IgnoreQueryFilters().CountAsync();
            logsBefore = await db.InforSyncLogs.IgnoreQueryFilters().CountAsync(l => l.Direction == SyncDirection.Outbound);
        }

        await DrainAsync();

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.Id == rowId);
            row.Status.Should().Be(OutboxStatus.Skipped, "a gate refusal is a decision, not a failure");
            row.SkipReason.Should().Contain("false");
            row.GateVersion.Should().Be(11, "the gate version in force at the re-check is stamped");
            row.LastError.Should().BeNull();

            (await db.IntegrationErrors.IgnoreQueryFilters().CountAsync())
                .Should().Be(errorsBefore, "no IntegrationError for a Skipped row (D-R9-9)");
            (await db.InforSyncLogs.IgnoreQueryFilters().CountAsync(l => l.Direction == SyncDirection.Outbound))
                .Should().Be(logsBefore, "no LN call, no SyncLog");
        }
        await CleanupAsync();
    }

    [SkippableFact]
    public async Task Gate_change_after_enqueue_does_not_retro_skip_the_row()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // R12 (D17) — "activating or tightening a gate affects transactions from that point forward, and
        // leaves everything already in flight alone."
        //
        // Without the gateVersion match this row would be Skipped, and a Skip is TERMINAL: the business event
        // would never reach LN and nothing would look broken. The row was legitimately enqueued under the gate
        // that existed at the time; a gate authored afterwards has no say over it.
        await CleanupAsync();
        await SetInvoiceGateAsync("invoiceStatus = \"UnderReview\"", gateVersion: 11);
        var rowId = await EnqueuePendingAsync(Guid.NewGuid().ToString("N")[..8], gateVersion: 10);

        await DrainAsync();

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.Id == rowId);
            row.Status.Should().NotBe(OutboxStatus.Skipped,
                "a gate introduced after this row was enqueued must not reach backwards and terminally skip it");
            row.SkipReason.Should().BeNull();
        }
        await CleanupAsync();
    }

    [SkippableFact]
    public async Task Global_kill_holds_dispatch_while_enqueue_continues_then_drains_on_reenable()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await CleanupAsync();

        // Kill the tenant's global outbound.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.IntegrationSwitches.Add(new IntegrationSwitch
            {
                Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
                Scope = IntegrationSwitchScope.OutboundGlobal, IsEnabled = false,
                LastReason = "test kill", CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Enqueue is untouched by the kill (D-R9-11) — the row lands Pending and accumulates.
        var rowId = await EnqueuePendingAsync(Guid.NewGuid().ToString("N")[..8]);
        await DrainAsync();
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.Id == rowId);
            row.Status.Should().Be(OutboxStatus.Pending, "a killed tenant's rows are never claimed");
            row.AttemptCount.Should().Be(0);
        }

        // Re-enable → the very next drain dispatches FIFO.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.IntegrationSwitches.IgnoreQueryFilters()
                .Where(s => s.TenantId == IntegrationTestFixture.TenantId && s.Scope == IntegrationSwitchScope.OutboundGlobal)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, true));
        }
        await DrainAsync();
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.Id == rowId);
            row.Status.Should().Be(OutboxStatus.Dispatched);
        }
        await CleanupAsync();
    }
}
