namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Low-level raw-email transport. The Mock / SMTP implementations both expose this — used by
/// the EmailOutboxWorker BackgroundService to send rendered subject+body pairs. Kept distinct
/// from <see cref="IEmailService"/> (which is the typed-message contract handlers enqueue
/// through) so the worker never accidentally re-enqueues a row it's draining.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}
