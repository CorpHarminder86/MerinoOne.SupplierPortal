using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
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
/// R9 (D-R9-2/D-R9-11) — tri-state dispatch routing through the REAL worker drain (Mock mode):
/// no config / Legacy → the compiled path, byte-identical; Held → the row is never claimed;
/// Dynamic → JSONata pipeline with the canonical payload in the SyncLog; flip-back is reversible;
/// permanent config failures stamp the [permanent] prefix + IntegrationError marker.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LnDispatchRoutingTests
{
    private readonly IntegrationTestFixture _fx;
    public LnDispatchRoutingTests(IntegrationTestFixture fx) => _fx = fx;

    private async Task DrainAsync()
    {
        var sf = _fx.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var cfg = _fx.Factory.Services.GetRequiredService<IConfiguration>();
        var worker = new OutboxDispatcherWorker(sf, NullLogger<OutboxDispatcherWorker>.Instance, cfg);
        await worker.DrainOnceAsync(CancellationToken.None);
    }

    private async Task<Guid> EnqueueInvoicePostAsync(string tag)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = IntegrationTestFixture.TenantId,
            TransactionType = OutboxTransactionType.InvoicePost,
            EntityName = OutboxEntity.Invoice,
            EntityId = IntegrationTestFixture.InvoiceId,
            DeterministicKey = OutboxKey.For(OutboxEntity.Invoice, IntegrationTestFixture.TenantId, $"route-{tag}", "post"),
            Status = OutboxStatus.Pending,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow,
        };
        db.OutboxMessages.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Upserts the tenant's InvoicePost config in the given mode (repo default expressions).</summary>
    private async Task SetInvoiceConfigAsync(LnDispatchMode mode, string? requestExprOverride = null)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var defaults = new LnDefaultExpressions();
        var entry = defaults.TryGet(OutboxTransactionType.InvoicePost)!;

        var cfg = await db.LnEndpointConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == IntegrationTestFixture.TenantId
                && c.TransactionType == OutboxTransactionType.InvoicePost && !c.IsDeleted);
        if (cfg is null)
        {
            cfg = new LnEndpointConfig
            {
                TenantId = IntegrationTestFixture.TenantId,
                TransactionType = OutboxTransactionType.InvoicePost,
                PortalEntity = LnPortalEntity.Invoice,
                EndpointPath = "LN/lnapi/odata/cisli.selfBillingInvoices/Invoices",
                CreatedBy = "seed",
            };
            db.LnEndpointConfigs.Add(cfg);
        }
        cfg.DispatchMode = mode;
        cfg.RequestMappingExpr = requestExprOverride ?? entry.RequestExpr;
        cfg.ResponseMappingExpr = entry.ResponseExpr;
        await db.SaveChangesAsync();
    }

    private async Task RemoveInvoiceConfigAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.LnEndpointConfigs.IgnoreQueryFilters()
            .Where(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.InvoicePost)
            .ExecuteDeleteAsync();
    }

    private async Task<OutboxMessage> RowAsync(Guid rowId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.Id == rowId);
    }

    [SkippableFact]
    public async Task No_config_row_dispatches_legacy()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await RemoveInvoiceConfigAsync();
        var rowId = await EnqueueInvoicePostAsync(Guid.NewGuid().ToString("N")[..8]);

        await DrainAsync();

        var row = await RowAsync(rowId);
        row.Status.Should().Be(OutboxStatus.Dispatched); // Mock legacy path: success, awaiting erp-ack
        row.LastError.Should().BeNull();
    }

    [SkippableFact]
    public async Task Legacy_mode_config_row_changes_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SetInvoiceConfigAsync(LnDispatchMode.Legacy);
        var rowId = await EnqueueInvoicePostAsync(Guid.NewGuid().ToString("N")[..8]);

        await DrainAsync();

        (await RowAsync(rowId)).Status.Should().Be(OutboxStatus.Dispatched);
        await RemoveInvoiceConfigAsync();
    }

    [SkippableFact]
    public async Task Held_mode_leaves_the_row_pending_and_unclaimed()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SetInvoiceConfigAsync(LnDispatchMode.Held);
        var rowId = await EnqueueInvoicePostAsync(Guid.NewGuid().ToString("N")[..8]);

        await DrainAsync();

        var row = await RowAsync(rowId);
        row.Status.Should().Be(OutboxStatus.Pending);   // never claimed — FIFO preserved for un-hold
        row.AttemptCount.Should().Be(0);

        // Un-hold → the very next drain dispatches it (kill stops dispatch, never enqueue — D-R9-11).
        await SetInvoiceConfigAsync(LnDispatchMode.Dynamic);
        await DrainAsync();
        (await RowAsync(rowId)).Status.Should().Be(OutboxStatus.Dispatched);
        await RemoveInvoiceConfigAsync();
    }

    [SkippableFact]
    public async Task Dynamic_mock_dispatch_logs_the_canonical_jsonata_payload()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SetInvoiceConfigAsync(LnDispatchMode.Dynamic);
        var rowId = await EnqueueInvoicePostAsync(Guid.NewGuid().ToString("N")[..8]);

        await DrainAsync();

        var row = await RowAsync(rowId);
        row.Status.Should().Be(OutboxStatus.Dispatched); // Mock returns no ErpCode → awaits erp-ack, like legacy
        row.AckedAt.Should().BeNull();

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var log = await db.InforSyncLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.IdempotencyKey == row.DeterministicKey && l.Status == SyncStatus.Success)
                .OrderByDescending(l => l.CreatedOn).FirstAsync();

            // The SyncLog carries the CANONICAL JSONata payload — byte-identical to the legacy builder's
            // canonical form (the pipeline-level parity check, on top of the dedicated harness).
            var legacyJson = await InvoiceOutboundPayloadBuilder.BuildJsonAsync(db, IntegrationTestFixture.InvoiceId);
            log.PayloadJson.Should().Be(LnJson.CanonicalWrite(legacyJson!));
        }

        // Reversibility (D-R9-2): flip back to Legacy → the next row routes through the compiled path.
        await SetInvoiceConfigAsync(LnDispatchMode.Legacy);
        var backId = await EnqueueInvoicePostAsync(Guid.NewGuid().ToString("N")[..8]);
        await DrainAsync();
        (await RowAsync(backId)).Status.Should().Be(OutboxStatus.Dispatched);
        await RemoveInvoiceConfigAsync();
    }

    [SkippableFact]
    public async Task Dynamic_request_mapping_failure_is_permanent_with_marker()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // A request expression that evaluates to nothing (navigates off the document) = a CONFIG bug:
        // permanent Failed, no LN call, [permanent] prefix + IntegrationError marker (D-R9-5).
        await SetInvoiceConfigAsync(LnDispatchMode.Dynamic, requestExprOverride: "no.such.path");
        var rowId = await EnqueueInvoicePostAsync(Guid.NewGuid().ToString("N")[..8]);

        await DrainAsync();

        var row = await RowAsync(rowId);
        row.Status.Should().Be(OutboxStatus.Failed);
        row.LastError.Should().StartWith(LnRetriabilityClassifier.PermanentLastErrorPrefix);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var err = await db.IntegrationErrors.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.StackTrace == LnRetriabilityClassifier.PermanentErrorMarker && !e.IsResolved)
                .OrderByDescending(e => e.CreatedOn).FirstOrDefaultAsync();
            err.Should().NotBeNull("a permanent dynamic failure must carry the ln-permanent-4xx marker");
        }
        await RemoveInvoiceConfigAsync();
    }
}
