namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Stage 1 transactional email gateway. The mock implementation writes to Serilog
/// and to logs/emails-YYYYMMDD.log so QA can fish out OTPs without an SMTP server.
/// </summary>
public interface IEmailService
{
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);

    Task SendWelcomeEmailAsync(
        string toEmail,
        string fullName,
        string userCode,
        string oneTimePassword,
        string loginUrl,
        CancellationToken ct = default);
}
