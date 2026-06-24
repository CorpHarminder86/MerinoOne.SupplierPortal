using FluentAssertions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;
using MerinoOne.SupplierPortal.Infrastructure.Persistence;
using MerinoOne.SupplierPortal.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Integration;

/// <summary>
/// FIX #2 — the stranded-<c>Dispatched</c> reconciliation sweep. A row whose POST landed (<c>Dispatched</c>) but
/// whose <c>/inbound/erp-ack</c> callback never arrived must raise EXACTLY ONE operator-visible
/// <see cref="IntegrationError"/> once it ages past the threshold — and must NOT re-raise on every subsequent sweep.
///
/// <para>Drives <see cref="OutboxDispatcherWorker.ReconcileStaleDispatchedAsync"/> directly (the worker's hosted
/// loop is awkward to wait on deterministically) against the REAL test DB, so the EF/SQL de-dupe (the LastError
/// one-time marker) is exercised exactly as it runs in production.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class OutboxStaleDispatchedSweepTests
{
    private readonly IntegrationTestFixture _fx;
    public OutboxStaleDispatchedSweepTests(IntegrationTestFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task Stale_dispatched_row_raises_exactly_one_alert_and_is_not_re_alerted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var rowId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var dispatchedAt = DateTime.UtcNow.AddMinutes(-90); // older than the 30-min threshold used below.

        // Seed a Dispatched (POST-landed, never-Acked) outbox row. CreatedBy="seed" short-circuits the audit
        // interceptor; LastError=null marks "not yet alerted"; AckedAt=null = no ack arrived.
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = rowId,
            TenantId = IntegrationTestFixture.TenantId,
            TransactionType = OutboxTransactionType.InvoicePost,
            EntityName = OutboxEntity.Invoice,
            EntityId = entityId,
            DeterministicKey = $"stale-dispatched-{tag}",
            Status = OutboxStatus.Dispatched,
            AttemptCount = 1,
            DispatchedAt = dispatchedAt,
            AckedAt = null,
            LastError = null,
            CreatedBy = "seed",
            CreatedOn = dispatchedAt,
        });
        await db.SaveChangesAsync();

        var threshold = TimeSpan.FromMinutes(30);

        int AlertCount()
        {
            using var s = _fx.Factory.Services.CreateScope();
            var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
            return d.IntegrationErrors.IgnoreQueryFilters().Count(e =>
                e.TenantId == IntegrationTestFixture.TenantId
                && e.EntityName == OutboxEntity.Invoice
                && e.CreatedBy == "outbox-dispatcher"
                && e.ErrorMessage.Contains(entityId.ToString()));
        }

        // ---- First sweep raises exactly one alert for the stranded row. ----
        var raisedFirst = await OutboxDispatcherWorker.ReconcileStaleDispatchedAsync(
            db, threshold, batchSize: 25, NullLogger.Instance, CancellationToken.None);
        raisedFirst.Should().Be(1, because: "the stranded Dispatched row is past the threshold and unalerted");
        AlertCount().Should().Be(1, because: "exactly one IntegrationError is raised for the row");

        // The row was stamped with the de-dupe marker (no longer 'not yet alerted').
        var lastError = await db.OutboxMessages.IgnoreQueryFilters()
            .Where(m => m.Id == rowId).Select(m => m.LastError).FirstAsync();
        lastError.Should().NotBeNull(because: "the sweep stamped the one-time alerted marker");
        db.OutboxMessages.IgnoreQueryFilters().First(m => m.Id == rowId).Status
            .Should().Be(OutboxStatus.Dispatched, because: "alert-only — the sweep does NOT auto-resend or change status");

        // ---- Second sweep raises nothing (de-dupe holds). ----
        using var scope2 = _fx.Factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var raisedSecond = await OutboxDispatcherWorker.ReconcileStaleDispatchedAsync(
            db2, threshold, batchSize: 25, NullLogger.Instance, CancellationToken.None);
        raisedSecond.Should().Be(0, because: "an already-alerted row must not be re-alerted on a later sweep");
        AlertCount().Should().Be(1, because: "still exactly one alert after the second pass");
    }

    [SkippableFact]
    public async Task Fresh_dispatched_row_within_threshold_is_not_alerted()
    {
        Skip.IfNot(_fx.DbAvailable, $"needs SQL test DB ({_fx.DbUnavailableReason})");

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var entityId = Guid.NewGuid();
        var dispatchedAt = DateTime.UtcNow.AddMinutes(-5); // well within the 30-min threshold.

        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = IntegrationTestFixture.TenantId,
            TransactionType = OutboxTransactionType.AsnPost,
            EntityName = OutboxEntity.Asn,
            EntityId = entityId,
            DeterministicKey = $"fresh-dispatched-{tag}",
            Status = OutboxStatus.Dispatched,
            AttemptCount = 1,
            DispatchedAt = dispatchedAt,
            AckedAt = null,
            LastError = null,
            CreatedBy = "seed",
            CreatedOn = dispatchedAt,
        });
        await db.SaveChangesAsync();

        var raised = await OutboxDispatcherWorker.ReconcileStaleDispatchedAsync(
            db, TimeSpan.FromMinutes(30), batchSize: 25, NullLogger.Instance, CancellationToken.None);

        using var s = _fx.Factory.Services.CreateScope();
        var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var alerts = d.IntegrationErrors.IgnoreQueryFilters().Count(e =>
            e.CreatedBy == "outbox-dispatcher" && e.ErrorMessage.Contains(entityId.ToString()));
        alerts.Should().Be(0, because: "a row still within the threshold is not yet stranded");
    }
}
