using System.Text.Json;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Inbound;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Inbound;

/// <summary>
/// R9 (TSD R9 §2.6, D-R9-11 inbound scope) — replays held erp-ack batches once the tenant's
/// <c>InboundErpAck</c> switch re-enables: strictly FIFO (clustered Seq order), original idempotency key
/// replayed verbatim (an ack that already processed pre-kill dedupes via the executor's prior-Success
/// check), the switch re-checked between rows (a re-kill mid-replay pauses cleanly). After
/// <see cref="HeldInboundMessage.MaxReplayAttempts"/> failures the row goes <c>Failed</c> + ONE
/// IntegrationError (operator surface).
/// </summary>
internal sealed class HeldInboundReplayWorker : BackgroundService
{
    private const string IntervalConfigKey = "Integration:HeldInboundReplayIntervalSeconds";
    private const int DefaultIntervalSeconds = 60;
    private const int BatchSize = 25;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HeldInboundReplayWorker> _logger;
    private readonly TimeSpan _interval;

    public HeldInboundReplayWorker(IServiceScopeFactory scopeFactory, ILogger<HeldInboundReplayWorker> logger, IConfiguration cfg)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(int.TryParse(cfg[IntervalConfigKey], out var s) && s >= 5 ? s : DefaultIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HeldInboundReplayWorker started. Interval={Interval}s Batch={Batch}", _interval.TotalSeconds, BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReplayOnceAsync(_scopeFactory, _logger, stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Held-inbound replay pass failed.");
            }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { /* shutdown */ }
        }
    }

    /// <summary>Testable core. Returns the number of held rows replayed successfully this pass.</summary>
    internal static async Task<int> ReplayOnceAsync(IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();

        // Tenants with held rows whose inbound switch is back ON (absent row = enabled).
        var heldTenants = await db.HeldInboundMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(h => !h.IsDeleted && h.Status == "Held" && h.TenantId != null)
            .Select(h => h.TenantId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (heldTenants.Count == 0) return 0;

        var stillKilled = (await db.IntegrationSwitches.IgnoreQueryFilters().AsNoTracking()
                .Where(s => !s.IsDeleted && !s.IsEnabled && s.Scope == IntegrationSwitchScope.InboundErpAck
                            && s.TenantId != null && heldTenants.Contains(s.TenantId.Value))
                .Select(s => s.TenantId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        var replayed = 0;
        foreach (var tenantId in heldTenants.Where(t => !stillKilled.Contains(t)))
        {
            // FIFO by the clustered Seq — held acks replay in arrival order.
            var rows = await db.HeldInboundMessages.IgnoreQueryFilters().AsNoTracking()
                .Where(h => !h.IsDeleted && h.Status == "Held" && h.TenantId == tenantId)
                .OrderBy(h => h.Seq)
                .Take(BatchSize)
                .ToListAsync(ct);

            foreach (var held in rows)
            {
                if (ct.IsCancellationRequested) break;

                // Re-check the switch per row — a re-kill mid-replay pauses cleanly, leaving the rest Held.
                var reKilled = await db.IntegrationSwitches.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(s => !s.IsDeleted && !s.IsEnabled && s.TenantId == tenantId
                                   && s.Scope == IntegrationSwitchScope.InboundErpAck, ct);
                if (reKilled) break;

                // Per-row scope: each replay is its own unit of work (a poisoned batch must not stall the queue).
                using var rowScope = scopeFactory.CreateScope();
                var rowDb = rowScope.ServiceProvider.GetRequiredService<IAppDbContext>();
                var mediator = rowScope.ServiceProvider.GetRequiredService<IMediator>();
                var now = DateTime.UtcNow;
                try
                {
                    var body = JsonSerializer.Deserialize<PushErpAckRequest>(held.PayloadJson)
                        ?? throw new InvalidOperationException("Held payload is unreadable.");
                    var boundIds = string.IsNullOrWhiteSpace(held.BoundCompanyIdsJson)
                        ? new HashSet<Guid>()
                        : JsonSerializer.Deserialize<HashSet<Guid>>(held.BoundCompanyIdsJson) ?? new HashSet<Guid>();

                    await mediator.Send(new UpsertErpAckCommand(body, boundIds, held.IdempotencyKey, ReplayTenantId: tenantId), ct);

                    await rowDb.HeldInboundMessages.IgnoreQueryFilters()
                        .Where(h => h.Id == held.Id && h.Status == "Held")
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(h => h.Status, "Replayed")
                            .SetProperty(h => h.ReplayedOn, now)
                            .SetProperty(h => h.LastError, (string?)null)
                            .SetProperty(h => h.UpdatedBy, "held-inbound-replay")
                            .SetProperty(h => h.UpdatedOn, now), ct);
                    replayed++;
                }
                catch (Exception ex)
                {
                    var attempts = held.ReplayAttempts + 1;
                    var exhausted = attempts >= HeldInboundMessage.MaxReplayAttempts;
                    await rowDb.HeldInboundMessages.IgnoreQueryFilters()
                        .Where(h => h.Id == held.Id && h.Status == "Held")
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(h => h.ReplayAttempts, attempts)
                            .SetProperty(h => h.Status, exhausted ? "Failed" : "Held")
                            .SetProperty(h => h.LastError, Truncate(ex.Message, 2000))
                            .SetProperty(h => h.UpdatedBy, "held-inbound-replay")
                            .SetProperty(h => h.UpdatedOn, now), ct);
                    if (exhausted)
                    {
                        rowDb.IntegrationErrors.Add(new IntegrationError
                        {
                            Id = Guid.NewGuid(),
                            TenantId = tenantId,
                            EntityName = "ErpAck",
                            ErrorMessage = Truncate($"Held erp-ack replay exhausted after {attempts} attempts: {ex.Message}", 2000),
                            StackTrace = "held-inbound-replay-exhausted",
                            RetryCount = attempts,
                            IsResolved = false,
                            CreatedBy = "held-inbound-replay",
                            CreatedOn = now,
                        });
                        await rowDb.SaveChangesAsync(ct);
                    }
                    logger.LogWarning(ex, "[HeldReplay] Replay failed for held row {Id} (attempt {Attempt}/{Max}).",
                        held.Id, attempts, HeldInboundMessage.MaxReplayAttempts);
                }
            }
        }
        return replayed;
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
