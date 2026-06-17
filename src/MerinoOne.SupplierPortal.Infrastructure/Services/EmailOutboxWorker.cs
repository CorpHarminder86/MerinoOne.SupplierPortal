using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// Drains <c>admin.emailOutbox</c>. Polls every <see cref="PollInterval"/> for rows with
/// <c>status = Pending</c> and <c>nextAttemptAt &lt;= now</c>, takes a batch, marks them
/// <c>Sending</c> (lease), dispatches via <see cref="IEmailSender"/>, then transitions to
/// <c>Sent</c> on success or schedules a retry on failure. After <see cref="MaxAttempts"/>
/// failures the row flips to <c>DeadLetter</c> and a warning is logged.
///
/// Backoff schedule (per attempt index): 1m, 5m, 30m, 2h, 6h — about a 9-hour total retry
/// window, generous enough to ride out short SMTP outages without spinning hot.
/// </summary>
internal sealed class EmailOutboxWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 25;
    private const int MaxAttempts = 5;

    /// <summary>Backoff indexed by previous AttemptCount (1-based). Length must equal <see cref="MaxAttempts"/>.</summary>
    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailOutboxWorker> _logger;

    public EmailOutboxWorker(IServiceScopeFactory scopeFactory, ILogger<EmailOutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EmailOutboxWorker started. Poll={Poll}s Batch={Batch} MaxAttempts={Max}",
            PollInterval.TotalSeconds, BatchSize, MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Last-resort guard so a single broken row never kills the worker.
                _logger.LogError(ex, "EmailOutboxWorker pump iteration failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException) { /* shutdown */ }
        }

        _logger.LogInformation("EmailOutboxWorker stopped.");
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        var now = DateTime.UtcNow;
        // IgnoreQueryFilters: this worker is a SYSTEM component that must drain EVERY tenant's outbox. It runs in a
        // background scope with no HttpContext, so ICurrentUser.TenantId is null — the always-on tenant filter would
        // otherwise match zero rows (EmailOutbox is ITenantOwned) and silently strand every message once rows carry a
        // TenantId and Scope.FiltersEnabled is on. Re-apply the soft-delete guard explicitly (IgnoreQueryFilters drops it).
        var batch = await db.EmailOutbox
            .IgnoreQueryFilters()
            .Where(x => !x.IsDeleted && x.Status == EmailOutboxStatus.Pending && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return;

        // Lease — flip to Sending in one round-trip so a re-entry of this worker (e.g. multiple
        // instances behind a load balancer) won't double-send.
        foreach (var row in batch) row.Status = EmailOutboxStatus.Sending;
        await db.SaveChangesAsync(ct);

        foreach (var row in batch)
        {
            if (ct.IsCancellationRequested) break;
            await TrySendAsync(db, sender, row, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task TrySendAsync(IAppDbContext db, IEmailSender sender, EmailOutbox row, CancellationToken ct)
    {
        try
        {
            await sender.SendAsync(row.ToEmail, row.Subject, row.HtmlBody, ct);
            row.Status = EmailOutboxStatus.Sent;
            row.SentAt = DateTime.UtcNow;
            row.AttemptCount += 1;
            row.LastError = null;
            row.UpdatedBy = "outbox-worker";
            row.UpdatedOn = DateTime.UtcNow;
            _logger.LogInformation("[Outbox] Sent {Id} key={Key} to={To} (attempt {Attempt}).",
                row.Id, row.TemplateKey, row.ToEmail, row.AttemptCount);
        }
        catch (Exception ex)
        {
            row.AttemptCount += 1;
            row.LastError = Truncate(ex.Message, 2000);
            row.UpdatedBy = "outbox-worker";
            row.UpdatedOn = DateTime.UtcNow;

            if (row.AttemptCount >= MaxAttempts)
            {
                row.Status = EmailOutboxStatus.DeadLetter;
                _logger.LogWarning(ex,
                    "[Outbox] Dead-lettered {Id} key={Key} to={To} after {Attempts} attempts.",
                    row.Id, row.TemplateKey, row.ToEmail, row.AttemptCount);
            }
            else
            {
                // Schedule next retry. AttemptCount is 1-based by now; index off-by-one is intentional.
                var delay = BackoffSchedule[Math.Min(row.AttemptCount - 1, BackoffSchedule.Length - 1)];
                row.NextAttemptAt = DateTime.UtcNow.Add(delay);
                row.Status = EmailOutboxStatus.Pending;
                _logger.LogWarning(ex,
                    "[Outbox] Send failed for {Id} key={Key} to={To}. Attempt {Attempt}/{Max}. Next retry in {Delay}.",
                    row.Id, row.TemplateKey, row.ToEmail, row.AttemptCount, MaxAttempts, delay);
            }
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
