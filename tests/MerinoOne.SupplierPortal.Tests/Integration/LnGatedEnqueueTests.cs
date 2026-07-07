using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LnInfra = MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using OutboxInfra = MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R9 (B3) — the gated enqueue chokepoint: legacy fallthrough with no/Legacy config; gate-true inserts
/// with gateVersion; gate-false creates NOTHING; a Skipped/Failed row on the same deterministic key
/// re-arms IN PLACE (same row id — O-R9-4's mechanism); live rows dedupe; Held still enqueues.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LnGatedEnqueueTests
{
    private readonly IntegrationTestFixture _fx;
    public LnGatedEnqueueTests(IntegrationTestFixture fx) => _fx = fx;

    private static string NewKey(string tag)
        => OutboxKey.For(OutboxEntity.Invoice, IntegrationTestFixture.TenantId, $"gated-{tag}", "post");

    private async Task<GatedEnqueueResult> EnqueueAsync(string key, LnInputDocOverrides? overrides = null)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var enqueuer = new OutboxInfra.LnGatedOutboxEnqueuer(
            db,
            new StubUser(),
            new LnInfra.LnEligibilityService(
                db,
                scope.ServiceProvider.GetRequiredService<ILnInputDocumentBuilderRegistry>(),
                scope.ServiceProvider.GetRequiredService<ILnMappingService>(),
                NullLogger<LnInfra.LnEligibilityService>.Instance),
            NullLogger<OutboxInfra.LnGatedOutboxEnqueuer>.Instance);
        var result = await enqueuer.EnqueueAsync(
            OutboxTransactionType.InvoicePost, OutboxEntity.Invoice, IntegrationTestFixture.InvoiceId,
            key, null, overrides: overrides);
        await db.SaveChangesAsync();
        return result;
    }

    private sealed class StubUser : MerinoOne.SupplierPortal.Application.Common.Interfaces.ICurrentUser
    {
        public string UserCode => "test-gated";
        public string? UserName => null;
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public IReadOnlyCollection<string> Permissions => Array.Empty<string>();
        public bool IsAuthenticated => true;
        public bool IsManager => false;
        public bool IsAdmin => true;
        public bool HasPermission(string code) => true;
        public Guid? TenantId => IntegrationTestFixture.TenantId;
        public bool IsPlatformAdmin => false;
    }

    /// <summary>Upsert the fixture tenant's InvoicePost config with the given mode + gate.</summary>
    private async Task SetConfigAsync(OutboundDispatchMode mode, string? gateExpr)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cfg = await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == IntegrationTestFixture.TenantId
                && c.TransactionType == OutboxTransactionType.InvoicePost && !c.IsDeleted);
        if (cfg is null)
        {
            var defaults = new LnInfra.LnDefaultExpressions();
            var entry = defaults.TryGet(OutboxTransactionType.InvoicePost)!;
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
        cfg.DispatchMode = mode;
        cfg.EligibilityGateExpr = gateExpr;
        cfg.GateVersion = 7;   // distinctive — stamped rows must carry it
        await db.SaveChangesAsync();
    }

    private async Task RemoveConfigAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .Where(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.InvoicePost)
            .ExecuteDeleteAsync();
    }

    private async Task<OutboxMessage?> RowByKeyAsync(string key)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == IntegrationTestFixture.TenantId && m.DeterministicKey == key && !m.IsDeleted);
    }

    [SkippableFact]
    public async Task No_config_enqueues_legacy()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await RemoveConfigAsync();
        var key = NewKey(Guid.NewGuid().ToString("N")[..8]);
        var result = await EnqueueAsync(key);
        result.Outcome.Should().Be(GatedEnqueueOutcome.EnqueuedLegacy);
        (await RowByKeyAsync(key))!.GateVersion.Should().BeNull();
    }

    [SkippableFact]
    public async Task Gate_true_enqueues_with_gateVersion_and_held_still_enqueues()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        // Fixture invoice is Submitted → gate passes. Held mode: kill stops dispatch, never enqueue (D-R9-11).
        await SetConfigAsync(OutboundDispatchMode.Held, "invoiceStatus = \"Submitted\"");
        var key = NewKey(Guid.NewGuid().ToString("N")[..8]);
        var result = await EnqueueAsync(key);
        result.Outcome.Should().Be(GatedEnqueueOutcome.Enqueued);
        var row = await RowByKeyAsync(key);
        row!.Status.Should().Be(OutboxStatus.Pending);
        row.GateVersion.Should().Be(7);
        await RemoveConfigAsync();
    }

    [SkippableFact]
    public async Task Gate_false_creates_nothing()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await SetConfigAsync(OutboundDispatchMode.Dynamic, "invoiceStatus = \"NoSuchStatus\"");
        var key = NewKey(Guid.NewGuid().ToString("N")[..8]);
        var result = await EnqueueAsync(key);
        result.Outcome.Should().Be(GatedEnqueueOutcome.GateIneligible);
        result.Reason.Should().Contain("false");
        (await RowByKeyAsync(key)).Should().BeNull("gate-ineligible must create NO row — the sweep catches later eligibility");
        await RemoveConfigAsync();
    }

    [SkippableFact]
    public async Task Skipped_and_failed_rows_rearm_in_place_and_live_rows_dedupe()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await RemoveConfigAsync();
        var key = NewKey(Guid.NewGuid().ToString("N")[..8]);

        // Seed a Skipped row on the key (the revoke-withdraw shape).
        Guid skippedRowId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = new OutboxMessage
            {
                Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
                TransactionType = OutboxTransactionType.InvoicePost, EntityName = OutboxEntity.Invoice,
                EntityId = IntegrationTestFixture.InvoiceId, DeterministicKey = key,
                Status = OutboxStatus.Skipped, SkipReason = "invoice revoked (pre-post)", GateVersion = 3,
                CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            };
            db.OutboxMessages.Add(row);
            await db.SaveChangesAsync();
            skippedRowId = row.Id;
        }

        // Re-enqueue on the SAME key → the SAME row re-arms (O-R9-4: no unique-index collision, no duplicate).
        var rearm = await EnqueueAsync(key);
        rearm.Outcome.Should().Be(GatedEnqueueOutcome.Rearmed);
        var rearmed = await RowByKeyAsync(key);
        rearmed!.Id.Should().Be(skippedRowId, "re-arm-over-create must reuse the SAME row (D-R9-10a)");
        rearmed.Status.Should().Be(OutboxStatus.Pending);
        rearmed.SkipReason.Should().BeNull();
        rearmed.LastError.Should().BeNull();

        // A live (Pending) row dedupes.
        var dedupe = await EnqueueAsync(key);
        dedupe.Outcome.Should().Be(GatedEnqueueOutcome.AlreadyLive);
    }
}
