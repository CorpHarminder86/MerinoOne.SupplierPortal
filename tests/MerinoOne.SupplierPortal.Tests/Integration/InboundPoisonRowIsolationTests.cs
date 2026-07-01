using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// FIX #1 — the resilient inbound flush. An UNANTICIPATED DB-constraint poison row (here: a Payment whose
/// <c>InvoiceId</c> violates <c>FK_Payment_Invoice_InvoiceId</c>, only surfaced at SaveChanges) must NOT roll the
/// whole batch back to a 500: the executor isolates the poison via <c>ex.Entries</c> (SQL SAVEPOINT + rollback-to-
/// savepoint), records it Failed with the SQL root cause, and commits the GOOD rows. The SyncLog then reports the
/// correct succeeded/failed counts + a failures JSON on the linked IntegrationError.
///
/// <para>The poison is forced by driving <see cref="InboundUpsertExecutor"/> directly (the task explicitly allows
/// this when a real DB constraint can't be tripped through the public endpoint, since every public upsert callback
/// validates its FKs in-memory first). The executor still runs its full company-resolution / anti-spoof / endpoint-
/// gate / idempotency / transactional SyncLog path against the REAL test DB — exactly the EF/SQL boundary the bug
/// lived at.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InboundPoisonRowIsolationTests
{
    private readonly IntegrationTestFixture _fx;
    public InboundPoisonRowIsolationTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Inbound_batch_with_a_db_constraint_poison_isolates_it_and_persists_the_good_rows()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Unique markers so the test is order- and re-run-independent.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var goodRef1 = $"PAY-OK-{tag}-1";
        var goodRef2 = $"PAY-OK-{tag}-2";
        var poisonRef = $"PAY-BAD-{tag}";
        var poisonInvoiceId = Guid.NewGuid(); // does NOT exist → FK violation at flush.

        var user = new StubCurrentUser(IntegrationTestFixture.TenantId);
        var exec = new InboundUpsertExecutor(
            db,
            user,
            new StubCurrentCompany(),
            // Every inbound call logs one integration.InforSyncLog row (+ a linked IntegrationError on failure);
            // the duplicate proc.SyncLog writer was dropped ([[r5-consolidation]]).
            NullLogger<InboundUpsertExecutor>.Instance);

        var codes = new[] { goodRef1, goodRef2, poisonRef };
        var canonical = codes.Select(c => $"poison-test|{c}");

        // 20-row-style batch shape: 2 good + 1 poison. The callback adds entities to the SAME db the executor flushes.
        var result = await exec.ExecuteAsync(
            TransactionalInboundEntity.Payment,
            IntegrationTestFixture.CompanyCode,
            new HashSet<Guid> { IntegrationTestFixture.CompanyId },
            idempotencyKey: $"poison-isolation-{tag}",
            received: 3,
            canonicalRows: canonical,
            codes: codes,
            requestPayload: new { tag, codes },
            upsertAsync: (ctx, tenantId, sourceId, ct) =>
            {
                var now = DateTime.UtcNow;

                Payment Make(string payRef, Guid invoiceId) => new()
                {
                    Id = Guid.NewGuid(),
                    PaymentReference = payRef,
                    InvoiceId = invoiceId,
                    SupplierId = IntegrationTestFixture.SupplierId,
                    PaymentDate = now,
                    PaymentAmount = 100m,
                    NetPaid = 100m,
                    SeccodeId = IntegrationTestFixture.SeccodeId,
                    TenantId = tenantId,
                    TenantEntityId = sourceId,
                    CreatedBy = "infor:inbound",
                    CreatedOn = now,
                };

                // Two GOOD rows on the seeded invoice + ONE poison row on a non-existent invoice (FK violation).
                ctx.Payments.Add(Make(goodRef1, IntegrationTestFixture.InvoiceId));
                ctx.Payments.Add(Make(goodRef2, IntegrationTestFixture.InvoiceId));
                ctx.Payments.Add(Make(poisonRef, poisonInvoiceId));

                IReadOnlyList<RowResult> rows = new List<RowResult>
                {
                    new(goodRef1, RowOutcome.Inserted, null),
                    new(goodRef2, RowOutcome.Inserted, null),
                    new(poisonRef, RowOutcome.Inserted, null),
                };
                return Task.FromResult(rows);
            },
            CancellationToken.None);

        // ---- The batch did NOT 500: it returned a normal result with accurate counts. ----
        result.Received.Should().Be(3);
        result.Failed.Should().Be(1, because: "exactly the one FK-poison row is isolated and failed");
        result.Inserted.Should().Be(2, because: "the two good rows still persisted");

        // ---- The two good rows persisted; the poison row did NOT. ----
        var goodCount = await db.Payments.IgnoreQueryFilters()
            .CountAsync(p => p.TenantId == IntegrationTestFixture.TenantId
                             && (p.PaymentReference == goodRef1 || p.PaymentReference == goodRef2));
        goodCount.Should().Be(2, because: "the resilient flush committed the good rows");

        var poisonPersisted = await db.Payments.IgnoreQueryFilters()
            .AnyAsync(p => p.PaymentReference == poisonRef);
        poisonPersisted.Should().BeFalse(because: "the poison row was detached, never committed");

        // ---- The SyncLog reports the correct succeeded/failed counts + a failures JSON on the linked error. ----
        var log = await db.InforSyncLogs.IgnoreQueryFilters()
            .Where(l => l.TenantId == IntegrationTestFixture.TenantId
                        && l.EntityName == nameof(TransactionalInboundEntity.Payment)
                        && l.Direction == SyncDirection.Inbound
                        && l.IdempotencyKey == $"poison-isolation-{tag}")
            .OrderByDescending(l => l.SyncedAt)
            .FirstOrDefaultAsync();
        log.Should().NotBeNull(because: "the executor always writes a SyncLog for the batch");
        log!.Status.Should().Be(SyncStatus.Failed, because: "the batch had a failed row");
        log.EntityCount.Should().Be(3, because: "EntityCount = received");
        log.ErrorMessage.Should().Contain("1 of 3 failed", because: "the message states the failed/received counts");
        log.ErrorMessage.Should().Contain(poisonRef, because: "the failure reason is now inlined on the SyncLog (not just 'see linked error')");

        var error = await db.IntegrationErrors.IgnoreQueryFilters()
            .Where(e => e.SyncLogId == log.Id)
            .FirstOrDefaultAsync();
        error.Should().NotBeNull(because: "a failed batch links an IntegrationError");
        error!.StackTrace.Should().NotBeNullOrWhiteSpace(because: "the failures JSON lives on the linked error detail");

        // The failures JSON is a parseable array containing the poison code + its DB root cause.
        using var doc = System.Text.Json.JsonDocument.Parse(error.StackTrace!);
        doc.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        var entries = doc.RootElement.EnumerateArray().ToList();
        entries.Should().ContainSingle(e => e.GetProperty("code").GetString() == poisonRef,
            because: "the failures JSON carries the poison row's business code");
        var poisonEntry = entries.Single(e => e.GetProperty("code").GetString() == poisonRef);
        poisonEntry.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace(
            because: "the per-row root cause (SQL FK violation message) is captured");

        // ---- The endpoint-map telemetry STILL persisted on the poison path (the tracked Unchanged map row must not
        //      be dropped by the isolation re-flush). ----
        var mapLastStatus = await db.InforEndpointMaps.IgnoreQueryFilters()
            .Where(m => m.TenantId == IntegrationTestFixture.TenantId
                        && m.EntityName == nameof(TransactionalInboundEntity.Payment)
                        && m.Direction == SyncDirection.Inbound)
            .Select(m => m.LastStatus)
            .FirstOrDefaultAsync();
        mapLastStatus.Should().Be(SyncStatus.Failed.ToString(),
            because: "the endpoint-session telemetry commits even when the batch had a poison row");
    }

    // -------------------- test doubles --------------------

    private sealed class StubCurrentUser(Guid tenantId) : ICurrentUser
    {
        public string UserCode => "infor:inbound";
        public string? UserName => "infor:inbound";
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public IReadOnlyCollection<string> Permissions => Array.Empty<string>();
        public bool IsAuthenticated => true;
        public bool IsManager => false;
        public bool IsAdmin => false;
        public bool HasPermission(string code) => false;
        public Guid? TenantId { get; } = tenantId;
        public bool IsPlatformAdmin => false;
    }

    private sealed class StubCurrentCompany : ICurrentCompany
    {
        public Guid? ActiveCompanyId => IntegrationTestFixture.CompanyId;
        public IReadOnlyCollection<Guid> AccessibleCompanyIds => new[] { IntegrationTestFixture.CompanyId };
        public bool ActiveCompanyFullAccess => false;
        // The transactional overload uses identity normalization, so this is not exercised; return identity anyway.
        public Guid? ResolveSource(SharedEndpoint endpoint, Guid? companyId) => companyId;
    }
}
