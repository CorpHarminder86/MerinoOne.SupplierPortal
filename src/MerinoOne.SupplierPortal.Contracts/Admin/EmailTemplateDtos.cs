namespace MerinoOne.SupplierPortal.Contracts.Admin;

/// <summary>
/// Wire model for an admin-editable email template row. <c>Notes</c> is an admin-facing
/// freetext hint describing which placeholders the body supports (e.g. "Placeholders:
/// {{legalName}}, {{registrationUrl}}, {{expiresAt}}"). It is rendered on the admin UI but
/// NOT consumed by the send pipeline.
/// </summary>
public record EmailTemplateDto(
    Guid Id,
    int Seq,
    string TemplateKey,
    string Subject,
    string HtmlBody,
    bool IsActive,
    string? Notes,
    DateTime CreatedOn,
    DateTime? UpdatedOn);

/// <summary>PUT body for editing a template. Identity is taken from the route key.</summary>
public record UpdateEmailTemplateRequest(string Subject, string HtmlBody, bool IsActive);

/// <summary>
/// POST body for the admin "send test" affordance. Subject + body are sent as-typed (with
/// dummy placeholder substitution applied first so admins can preview a populated render).
/// </summary>
public record SendTestEmailTemplateRequest(string ToEmail, string Subject, string HtmlBody);
