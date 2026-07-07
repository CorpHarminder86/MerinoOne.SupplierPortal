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

    private async Task SetInvoiceGateAsync(string gateExpr)
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
        cfg.GateVersion = 11;
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

    private async Task<Guid> EnqueuePendingAsync(string tag)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new OutboxMessage
        {
            Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
            TransactionType = OutboxTransactionType.InvoicePost, EntityName = OutboxEntity.Invoice,
            EntityId = IntegrationTestFixture.InvoiceId,
            DeterministicKey = OutboxKey.For(OutboxEntity.Invoice, IntegrationTestFixture.TenantId, $"recheck-{tag}", "post"),
            Status = OutboxStatus.Pending, GateVersion = 10, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
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
        await SetInvoiceGateAsync("invoiceStatus = \"UnderReview\"");
        var rowId = await EnqueuePendingAsync(Guid.NewGuid().ToString("N")[..8]);

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
