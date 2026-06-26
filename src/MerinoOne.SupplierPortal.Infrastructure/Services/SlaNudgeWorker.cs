using System.Net;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// R4 (2026-06-26) — Phase 6 / UC-PO-04 (§14): 48h negotiation-SLA buyer nudge. A supplier-raised
/// <see cref="Domain.Entities.Proc.PurchaseOrderNegotiation"/> sits <c>Submitted</c> until the buyer approves/rejects.
/// When a buyer leaves a Submitted negotiation un-actioned for the SLA window (default 48h) this worker enqueues a
/// ONE-TIME reminder e-mail to that buyer and stamps <c>NudgeSentAt</c> so the same negotiation is never nudged twice.
///
/// <para><b>Why a BackgroundService (mirrors <see cref="EmailOutboxWorker"/> / OutboxDispatcherWorker):</b> no scheduler
/// existed in the repo (plan D4). This polls on <c>Fulfilment:SlaNudgeIntervalMinutes</c> (default 30) and on each pass,
/// in a fresh scoped DbContext, selects overdue negotiations, enqueues a Pending <c>admin.emailOutbox</c> row, then sets
/// <c>NudgeSentAt</c>. It writes ONLY the outbox row — actual SMTP is the EmailOutboxWorker's job.</para>
///
/// <para><b>IgnoreQueryFilters:</b> this is a SYSTEM component with no HttpContext, so <c>ICurrentUser.TenantId</c> is
/// null and the always-on tenant filter would match zero rows (PurchaseOrderNegotiation is tenant-scoped). We drop the
/// filters and re-apply the soft-delete guard explicitly — exactly as the other two background drains do.</para>
///
/// <para><b>Dedupe + no hot-loop:</b> <c>NudgeSentAt</c> is the dedupe stamp. If the buyer e-mail can't be resolved
/// (no <c>BuyerUserId</c>, inactive/blank user e-mail) we STILL stamp <c>NudgeSentAt</c> and skip the enqueue, then log a
/// warning — otherwise an un-resolvable buyer would re-select every pass forever (hot-loop). The negotiation still surfaces
/// in the buyer worklist; only the e-mail reminder is skipped.</para>
/// </summary>
internal sealed class SlaNudgeWorker : BackgroundService
{
    /// <summary>Poll interval (minutes). Config <c>Fulfilment:SlaNudgeIntervalMinutes</c>, default 30, floor 1.</summary>
    private const string IntervalConfigKey = "Fulfilment:SlaNudgeIntervalMinutes";
    private const int DefaultIntervalMinutes = 30;

    /// <summary>SLA window (hours) a Submitted negotiation may sit un-actioned before nudging. Config
    /// <c>Fulfilment:SlaNudgeHours</c>, default 48, floor 1.</summary>
    private const string SlaHoursConfigKey = "Fulfilment:SlaNudgeHours";
    private const int DefaultSlaHours = 48;

    /// <summary>Max negotiations processed per pass (cap, like the other workers' BatchSize).</summary>
    private const int BatchSize = 50;

    private const string TemplateKey = "PoNegotiationNudge";
    private const string Actor = "sla-nudge-worker";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlaNudgeWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _slaWindow;

    public SlaNudgeWorker(IServiceScopeFactory scopeFactory, ILogger<SlaNudgeWorker> logger, IConfiguration cfg)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var minutes = int.TryParse(cfg[IntervalConfigKey], out var m) && m >= 1 ? m : DefaultIntervalMinutes;
        _pollInterval = TimeSpan.FromMinutes(minutes);

        var hours = int.TryParse(cfg[SlaHoursConfigKey], out var h) && h >= 1 ? h : DefaultSlaHours;
        _slaWindow = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SlaNudgeWorker started. Poll={Poll}min Sla={Sla}h Batch={Batch}",
            _pollInterval.TotalMinutes, _slaWindow.TotalHours, BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await NudgeOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Last-resort guard so a single broken row never kills the worker.
                _logger.LogError(ex, "SlaNudgeWorker pass failed.");
            }

            try { await Task.Delay(_pollInterval, stoppingToken); }
            catch (TaskCanceledException) { /* shutdown */ }
        }

        _logger.LogInformation("SlaNudgeWorker stopped.");
    }

    private async Task NudgeOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var renderer = scope.ServiceProvider.GetRequiredService<IEmailTemplateRenderer>();
        await NudgeOnceAsync(db, renderer, _slaWindow, BatchSize, _logger, ct);
    }

    /// <summary>
    /// The testable core of the 48h negotiation-SLA nudge pass. Extracted as <c>internal static</c> (mirrors
    /// <c>OutboxDispatcherWorker.ReconcileStaleDispatchedAsync</c>) so a focused <c>[Integration]</c> test can drive
    /// it on the real DB without the hosted loop / a fixed wall-clock wait. Returns the number of reminder rows
    /// ENQUEUED this pass (un-resolvable-buyer rows are stamped + skipped, not counted). Behaviour-preserving — the
    /// instance pass delegates here.
    /// </summary>
    internal static async Task<int> NudgeOnceAsync(
        IAppDbContext db, IEmailTemplateRenderer renderer, TimeSpan slaWindow, int batchSize, ILogger logger,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - slaWindow;
        var enqueued = 0;

        // SELECTION PREDICATE (UC-PO-04): Submitted negotiations whose SubmittedAt is older than the SLA window and
        // that have never been nudged. IgnoreQueryFilters (system scope, no ambient tenant) + explicit !IsDeleted.
        var overdue = await db.PurchaseOrderNegotiations
            .IgnoreQueryFilters()
            .Where(n => !n.IsDeleted
                        && n.NegotiationStatus == PoNegotiationStatus.Submitted
                        && n.SubmittedAt <= cutoff
                        && n.NudgeSentAt == null)
            .OrderBy(n => n.SubmittedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (overdue.Count == 0) return enqueued;

        foreach (var negotiation in overdue)
        {
            if (ct.IsCancellationRequested) break;

            // Resolve the buyer e-mail via the PO's BuyerUserId. IgnoreQueryFilters here too (the PO + AppUser are
            // tenant-scoped and this worker has no ambient tenant). Active, non-blank e-mail required to send.
            var buyerEmail = await db.PurchaseOrders
                .IgnoreQueryFilters()
                .Where(po => po.Id == negotiation.PurchaseOrderId && po.BuyerUserId != null)
                .Join(db.AppUsers.IgnoreQueryFilters().Where(u => !u.IsDeleted && u.IsActive),
                    po => po.BuyerUserId, u => u.Id, (po, u) => u.Email)
                .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(buyerEmail))
            {
                // Un-resolvable buyer e-mail. STAMP NudgeSentAt anyway so this negotiation is NOT re-selected every
                // pass (hot-loop guard) — the negotiation still sits in the buyer worklist; only the reminder is skipped.
                negotiation.NudgeSentAt = now;
                negotiation.UpdatedBy = Actor;
                negotiation.UpdatedOn = now;
                logger.LogWarning(
                    "[SlaNudge] No active buyer e-mail for PO {Po} (negotiation {Id}); stamped NudgeSentAt to avoid re-selecting. E-mail skipped.",
                    negotiation.PoNumber, negotiation.Id);
                continue;
            }

            await EnqueueNudgeAsync(db, renderer, negotiation, buyerEmail.Trim(), now, ct);
            enqueued++;

            // Dedupe: NEVER nudge the same negotiation twice.
            negotiation.NudgeSentAt = now;
            negotiation.UpdatedBy = Actor;
            negotiation.UpdatedOn = now;

            logger.LogInformation("[SlaNudge] Enqueued 48h negotiation nudge for PO {Po} (negotiation {Id}) to {To}.",
                negotiation.PoNumber, negotiation.Id, buyerEmail);
        }

        await db.SaveChangesAsync(ct);
        return enqueued;
    }

    /// <summary>
    /// Writes a Pending <c>admin.emailOutbox</c> row (tenant-stamped from the negotiation) for the buyer reminder.
    /// Renders the admin-editable <c>PoNegotiationNudge</c> template if present (mirrors TemplateAwareEmailService),
    /// else falls back to a minimal inline body. The EmailOutboxWorker dispatches it.
    /// </summary>
    private static async Task EnqueueNudgeAsync(
        IAppDbContext db, IEmailTemplateRenderer renderer,
        Domain.Entities.Proc.PurchaseOrderNegotiation negotiation, string toEmail, DateTime now, CancellationToken ct)
    {
        var ageHours = (int)Math.Round((now - negotiation.SubmittedAt).TotalHours);

        var placeholders = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["toEmail"] = toEmail,
            ["poNumber"] = negotiation.PoNumber,
            ["submittedAt"] = negotiation.SubmittedAt.ToString("u"),
            ["ageHours"] = ageHours.ToString(),
        };

        string subject;
        string body;
        var rendered = await renderer.TryRenderAsync(TemplateKey, placeholders, ct);
        if (rendered is not null)
        {
            subject = rendered.Subject;
            body = rendered.HtmlBody;
        }
        else
        {
            subject = $"Action required — PO {negotiation.PoNumber} negotiation awaiting your review";
            body = BuildFallbackBody(negotiation.PoNumber, ageHours);
        }

        db.EmailOutbox.Add(new EmailOutbox
        {
            Id = Guid.NewGuid(),
            TenantId = negotiation.TenantId,   // tenant-stamp from the negotiation (worker has no ambient tenant).
            TemplateKey = TemplateKey,
            ToEmail = toEmail,
            Subject = subject,
            HtmlBody = body,
            Status = EmailOutboxStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedBy = Actor,
            CreatedOn = now,
        });
    }

    private static string BuildFallbackBody(string poNumber, int ageHours) => $"""
<!DOCTYPE html>
<html><body style="font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
  <h2 style="color:#0f3b5e;">Negotiation awaiting your review — PO {WebUtility.HtmlEncode(poNumber)}</h2>
  <p>A supplier negotiation on purchase order <b>{WebUtility.HtmlEncode(poNumber)}</b> has been awaiting your review for
     about <b>{ageHours} hours</b> and is past the 48-hour response target.</p>
  <p>Please open the portal and approve or reject the negotiation so the supplier can proceed.</p>
</body></html>
""";
}
