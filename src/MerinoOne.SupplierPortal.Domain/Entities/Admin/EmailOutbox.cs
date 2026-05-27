using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Admin;

/// <summary>
/// Async email queue row. Handlers enqueue (status=Pending) inside their own DB transaction
/// so persistence + send-intent commit atomically; the EmailOutboxWorker BackgroundService
/// drains rows in the background, retries transient SMTP failures with exponential backoff
/// (1m / 5m / 30m / 2h / 6h), and dead-letters after 5 attempts.
/// </summary>
public class EmailOutbox : AuditableEntity
{
    /// <summary>Template key the message was rendered from (e.g. "Invite"). Diagnostic only.</summary>
    public string TemplateKey { get; set; } = string.Empty;

    public string ToEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;

    /// <summary>Fully-rendered HTML body. Pre-substituted at enqueue time so the worker is dumb pipe.</summary>
    public string HtmlBody { get; set; } = string.Empty;

    public EmailOutboxStatus Status { get; set; } = EmailOutboxStatus.Pending;

    /// <summary>How many send attempts have completed (success or failure).</summary>
    public int AttemptCount { get; set; }

    /// <summary>Earliest UTC time the worker will attempt to send. Drives the backoff schedule.</summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set on success. Null until then.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>Last exception message — kept short (max 2000 chars). Truncated longer payloads.</summary>
    public string? LastError { get; set; }
}

public enum EmailOutboxStatus
{
    /// <summary>Awaiting first attempt or a retry. Worker picks rows where NextAttemptAt &lt;= now.</summary>
    Pending = 0,

    /// <summary>Worker has claimed the row for this attempt (lease). Reverts to Pending on failure.</summary>
    Sending = 1,

    /// <summary>Successfully delivered to SMTP.</summary>
    Sent = 2,

    /// <summary>5 attempts exhausted. Manual intervention required (admin grid in a later PR).</summary>
    DeadLetter = 3,
}
