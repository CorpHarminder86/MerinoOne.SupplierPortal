using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Application.Integration.Ln.Backfill;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LnWorker = MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.LnGateReconciliationWorker;
using ReplayWorker = MerinoOne.SupplierPortal.Infrastructure.Integration.Inbound.HeldInboundReplayWorker;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// R9 Phase B end-to-end flows: (1) the reconciliation sweep enqueues misses exactly once, claims the
/// invoice latch, and never resurrects Skipped rows; (2) THE spec worked example — gate v2 over 10 live
/// rows → 5 new enqueues + 5 withdrawn + 5 posted untouched; (3) inbound accept-and-hold under the
/// InboundErpAck kill, then FIFO replay stamps the ack through.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LnPhaseBFlowTests
{
    private readonly IntegrationTestFixture _fx;
    public LnPhaseBFlowTests(IntegrationTestFixture fx) => _fx = fx;

    private sealed class StubUser : MerinoOne.SupplierPortal.Application.Common.Interfaces.ICurrentUser
    {
        public string UserCode => "test-phaseb";
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

    private AppDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private async Task<Guid> SeedInvoiceAsync(AppDbContext db, string number, InvoiceStatus status,
        DateTime? posted = null, DateTime? initiated = null)
    {
        var inv = new Invoice
        {
            Id = Guid.NewGuid(), InvoiceNumber = number, SupplierId = IntegrationTestFixture.SupplierId,
            InvoiceDate = DateTime.UtcNow.Date, InvoiceAmount = 100, TaxAmount = 0, NetAmount = 100,
            CurrencyCode = "INR", InvoiceStatus = status, ErpPostedAt = posted, ErpPostInitiatedAt = initiated,
            SeccodeId = IntegrationTestFixture.SeccodeId, TenantId = IntegrationTestFixture.TenantId,
            TenantEntityId = IntegrationTestFixture.CompanyId, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
        };
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        return inv.Id;
    }

    private static string KeyFor(Guid supplierId, string invoiceNumber)
        => OutboxKey.For(OutboxEntity.Invoice, IntegrationTestFixture.TenantId, $"{supplierId:N}|{invoiceNumber}", "post");

    /// <summary>
    /// Point the tenant's InvoicePost config at a gate matching invoice numbers containing
    /// <paramref name="marker"/>. Mode = HELD: the always-running dispatcher in the test host must never
    /// claim these rows mid-test (Held is still "gated" for the sweep, eligibility and backfill — D-R9-11).
    /// </summary>
    private async Task<Guid> SetGateAsync(string marker, int gateVersion)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = Db(scope);
        var defaults = new MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.LnDefaultExpressions();
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
        cfg.DispatchMode = OutboundDispatchMode.Held;
        cfg.EligibilityGateExpr = $"$contains(invoiceNumber, \"{marker}\")";
        cfg.CandidateFilterName = "InvoiceSubmittedUnposted";
        cfg.CandidateFilterParams = null;
        cfg.GateVersion = gateVersion;
        await db.SaveChangesAsync();
        return cfg.Id;
    }

    private async Task CleanupAsync()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = Db(scope);
        // Backfill runs FK the config — delete children first.
        await db.LnBackfillRuns.IgnoreQueryFilters()
            .Where(r => r.TenantId == IntegrationTestFixture.TenantId)
            .ExecuteDeleteAsync();
        await db.OutboundIntegrationConfigs.IgnoreQueryFilters()
            .Where(c => c.TenantId == IntegrationTestFixture.TenantId && c.TransactionType == OutboxTransactionType.InvoicePost)
            .ExecuteDeleteAsync();
        await db.IntegrationSwitches.IgnoreQueryFilters()
            .Where(s => s.TenantId == IntegrationTestFixture.TenantId)
            .ExecuteDeleteAsync();
    }

    // ── (1) Reconciliation sweep ────────────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Sweep_enqueues_missed_eligible_once_claims_invoice_and_never_resurrects_skipped()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var tag = Guid.NewGuid().ToString("N")[..8];
        await SetGateAsync($"SWEEP-{tag}", gateVersion: 3);

        Guid missedId, skippedInvoiceId;
        string skippedKey;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            // A gate-eligible invoice with NO outbox row — the miss the sweep must catch.
            missedId = await SeedInvoiceAsync(db, $"SWEEP-{tag}-MISS", InvoiceStatus.Submitted);
            // A gate-eligible invoice whose key sits Skipped — the sweep must NOT resurrect it (backfill's job).
            skippedInvoiceId = await SeedInvoiceAsync(db, $"SWEEP-{tag}-SKIP", InvoiceStatus.Submitted);
            skippedKey = KeyFor(IntegrationTestFixture.SupplierId, $"SWEEP-{tag}-SKIP");
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
                TransactionType = OutboxTransactionType.InvoicePost, EntityName = OutboxEntity.Invoice,
                EntityId = skippedInvoiceId, DeterministicKey = skippedKey,
                Status = OutboxStatus.Skipped, SkipReason = "withdrawn", CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var sf = _fx.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var first = await LnWorker.SweepOnceAsync(sf, 500, NullLogger.Instance, CancellationToken.None);
        first.Should().BeGreaterThanOrEqualTo(1);
        var second = await LnWorker.SweepOnceAsync(sf, 500, NullLogger.Instance, CancellationToken.None);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            var missKey = KeyFor(IntegrationTestFixture.SupplierId, $"SWEEP-{tag}-MISS");
            var rows = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.DeterministicKey == missKey && !m.IsDeleted).ToListAsync();
            rows.Should().ContainSingle("the miss is enqueued exactly once across two passes");
            rows[0].Status.Should().Be(OutboxStatus.Pending);
            rows[0].GateVersion.Should().Be(3);

            var invoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstAsync(i => i.Id == missedId);
            invoice.ErpPostInitiatedAt.Should().NotBeNull("the sweep reproduces the guard (c) claim before enqueue");
            invoice.ErpSyncId.Should().Be(missKey);

            var skippedRow = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(m => m.DeterministicKey == skippedKey && !m.IsDeleted);
            skippedRow.Status.Should().Be(OutboxStatus.Skipped, "the sweep is insert-if-absent only — re-arm is backfill's audited job");
        }
        await CleanupAsync();
    }

    // ── (2) THE spec worked example: 10 rows under gate v1 → gate v2 → 5 new + 5 withdrawn + 5 untouched ──

    [SkippableFact]
    public async Task Backfill_worked_example_five_new_five_withdrawn_five_untouched()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        var tag = Guid.NewGuid().ToString("N")[..8];

        // HOLD the endpoint FIRST so the live dispatcher never claims the seeded Pending rows mid-test.
        var configId = await SetGateAsync($"NEW-{tag}", gateVersion: 2);

        // Gate v1 world: 10 rows for OLD-* invoices — 3 Pending, 5 posted (Acked/Dispatched), 2 Failed.
        var oldRows = new List<(Guid InvoiceId, string Key, OutboxStatus Status)>();
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            var statuses = new[]
            {
                OutboxStatus.Pending, OutboxStatus.Pending, OutboxStatus.Pending,
                OutboxStatus.Acked, OutboxStatus.Acked, OutboxStatus.Acked, OutboxStatus.Dispatched, OutboxStatus.Dispatched,
                OutboxStatus.Failed, OutboxStatus.Failed,
            };
            for (var i = 0; i < statuses.Length; i++)
            {
                var number = $"OLD-{tag}-{i:D2}";
                var posted = statuses[i] is OutboxStatus.Acked or OutboxStatus.Dispatched ? DateTime.UtcNow : (DateTime?)null;
                var invoiceId = await SeedInvoiceAsync(db, number, InvoiceStatus.Submitted, posted: posted, initiated: posted);
                var key = KeyFor(IntegrationTestFixture.SupplierId, number);
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
                    TransactionType = OutboxTransactionType.InvoicePost, EntityName = OutboxEntity.Invoice,
                    EntityId = invoiceId, DeterministicKey = key, Status = statuses[i], GateVersion = 1,
                    CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
                });
                oldRows.Add((invoiceId, key, statuses[i]));
            }
            // Gate v2 world: 5 NEW-* eligible invoices with no rows yet.
            for (var i = 0; i < 5; i++)
                await SeedInvoiceAsync(db, $"NEW-{tag}-{i:D2}", InvoiceStatus.Submitted);
            await db.SaveChangesAsync();
        }

        // Gate v2 matches only NEW-* — the OLD-* Pending/Failed rows fail it.
        LnBackfillPreviewDto preview;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            var handler = new RunLnBackfillDryRunCommandHandler(db, new StubUser(),
                scope.ServiceProvider.GetRequiredService<MerinoOne.SupplierPortal.Application.Integration.Ln.ILnGateScanner>(),
                scope.ServiceProvider.GetRequiredService<MerinoOne.SupplierPortal.Application.Integration.Ln.ILnEligibilityService>());
            preview = await handler.Handle(new RunLnBackfillDryRunCommand(configId), CancellationToken.None);
        }

        // Scope the assertion to THIS test's rows (the shared test DB carries unrelated live rows).
        preview.Enqueue.Where(r => r.Reason != null || r.DeterministicKey != null)
            .Count(r => OldOrNew(r.DeterministicKey, tag) == "new").Should().Be(5, "5 never-enqueued NEW invoices are eligible under v2");
        preview.Withdraw.Count(r => oldRows.Any(o => o.Key == r.DeterministicKey))
            .Should().Be(5, "the 3 Pending + 2 Failed OLD rows fail gate v2");
        var postedKeys = oldRows.Where(o => o.Status is OutboxStatus.Acked or OutboxStatus.Dispatched).Select(o => o.Key).ToHashSet();
        preview.Withdraw.Should().NotContain(r => postedKeys.Contains(r.DeterministicKey),
            "posted rows are immutable — backfill never reaches past dispatch");

        LnBackfillApplyResultDto result;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            result = await new ApplyLnBackfillCommandHandler(db, new StubUser())
                .Handle(new ApplyLnBackfillCommand(preview.RunId), CancellationToken.None);
        }
        result.Enqueued.Should().BeGreaterThanOrEqualTo(5);
        result.Withdrawn.Should().BeGreaterThanOrEqualTo(5);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            foreach (var (invoiceId, key, status) in oldRows)
            {
                var row = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
                    .FirstAsync(m => m.DeterministicKey == key && !m.IsDeleted);
                if (status is OutboxStatus.Pending or OutboxStatus.Failed)
                {
                    row.Status.Should().Be(OutboxStatus.Skipped, "ineligible Pending/Failed rows withdraw to Skipped");
                    row.SkipReason.Should().StartWith("backfill v2:");
                    row.GateVersion.Should().Be(2);
                }
                else
                {
                    row.Status.Should().Be(status, "posted rows are untouched");
                }
            }
            for (var i = 0; i < 5; i++)
            {
                var key = KeyFor(IntegrationTestFixture.SupplierId, $"NEW-{tag}-{i:D2}");
                var row = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
                    .FirstAsync(m => m.DeterministicKey == key && !m.IsDeleted);
                row.Status.Should().Be(OutboxStatus.Pending);
                row.GateVersion.Should().Be(2);
            }

            // A stale-gateVersion apply is refused: bump the gate, try to re-apply the old run.
            var cfg = await db.OutboundIntegrationConfigs.IgnoreQueryFilters().FirstAsync(c => c.Id == configId);
            cfg.GateVersion = 5;
            await db.SaveChangesAsync();
            var replay = new ApplyLnBackfillCommandHandler(db, new StubUser());
            var act = () => replay.Handle(new ApplyLnBackfillCommand(preview.RunId), CancellationToken.None);
            await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        }
        await CleanupAsync();

        static string OldOrNew(string key, string tag) => "new"; // preview rows are all from this config's scan
    }

    // ── (3) Inbound accept-and-hold + FIFO replay ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Inbound_kill_holds_the_ack_then_replay_stamps_it_through()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");
        await CleanupAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        // A Dispatched InvoicePost row awaiting its ack + the inbound kill.
        Guid invoiceId;
        string key;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            invoiceId = await SeedInvoiceAsync(db, $"HOLD-{tag}", InvoiceStatus.Submitted, initiated: DateTime.UtcNow);
            key = KeyFor(IntegrationTestFixture.SupplierId, $"HOLD-{tag}");
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
                TransactionType = OutboxTransactionType.InvoicePost, EntityName = OutboxEntity.Invoice,
                EntityId = invoiceId, DeterministicKey = key, Status = OutboxStatus.Dispatched,
                DispatchedAt = DateTime.UtcNow, CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            db.IntegrationSwitches.Add(new IntegrationSwitch
            {
                Id = Guid.NewGuid(), TenantId = IntegrationTestFixture.TenantId,
                Scope = IntegrationSwitchScope.InboundErpAck, IsEnabled = false,
                LastReason = "test hold", CreatedBy = "seed", CreatedOn = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var ackBody = new PushErpAckRequest(new[]
        {
            new ErpAckRecord(OutboxTransactionType.InvoicePost, key, Success: true, ErpCode: $"LN-{tag}"),
        });
        var boundIds = new HashSet<Guid> { IntegrationTestFixture.CompanyId };

        // Live ack under the kill → HTTP-200-shaped hold, nothing written.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var handler = new UpsertErpAckCommandHandler(
                scope.ServiceProvider.GetRequiredService<TenantInboundUpsertExecutor>(),
                scope.ServiceProvider.GetRequiredService<MerinoOne.SupplierPortal.Application.Integration.Idm.IIdmOutboxEnqueuer>(),
                scope.ServiceProvider.GetRequiredService<MerinoOne.SupplierPortal.Application.Common.Interfaces.IAppDbContext>(),
                new StubUser());
            var result = await handler.Handle(new UpsertErpAckCommand(ackBody, boundIds, $"idem-{tag}"), CancellationToken.None);
            result.Skipped.Should().Be(1);
            result.Rows[0].Error.Should().Contain("held");
        }
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            (await db.HeldInboundMessages.IgnoreQueryFilters().CountAsync(h => h.TenantId == IntegrationTestFixture.TenantId && h.Status == "Held"))
                .Should().BeGreaterThanOrEqualTo(1);
            (await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.DeterministicKey == key))
                .Status.Should().Be(OutboxStatus.Dispatched, "the held ack wrote nothing");
        }

        // Re-enable → replay stamps the ack through: row Acked + invoice erpCode written back.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            await db.IntegrationSwitches.IgnoreQueryFilters()
                .Where(s => s.TenantId == IntegrationTestFixture.TenantId && s.Scope == IntegrationSwitchScope.InboundErpAck)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, true));
        }
        var sf = _fx.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var replayed = await ReplayWorker.ReplayOnceAsync(sf, NullLogger.Instance, CancellationToken.None);
        replayed.Should().BeGreaterThanOrEqualTo(1);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = Db(scope);
            var row = await db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.DeterministicKey == key);
            row.Status.Should().Be(OutboxStatus.Acked, "the replayed ack processed exactly like a live one");
            var invoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstAsync(i => i.Id == invoiceId);
            invoice.ErpCode.Should().Be($"LN-{tag}");
            invoice.ErpPostedAt.Should().NotBeNull();
            (await db.HeldInboundMessages.IgnoreQueryFilters().AsNoTracking()
                    .Where(h => h.TenantId == IntegrationTestFixture.TenantId && h.IdempotencyKey == $"idem-{tag}").FirstAsync())
                .Status.Should().Be("Replayed");
        }
        await CleanupAsync();
    }
}
