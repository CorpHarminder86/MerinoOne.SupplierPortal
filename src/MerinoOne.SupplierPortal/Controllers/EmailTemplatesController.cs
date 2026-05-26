using MediatR;
using MerinoOne.SupplierPortal.Application.Admin.EmailTemplates;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Admin;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Admin CRUD for the admin-editable <c>EmailTemplate</c> table. Reads require
/// <c>Settings.Read</c>; mutations (edit, test-send) require <c>Settings.Write</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/email-templates")]
public class EmailTemplatesController : ControllerBase
{
    private readonly IMediator _mediator;
    public EmailTemplatesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Email template list")]
    [EndpointDescription(@"All admin-editable email templates with their current subject + body.
Returns: List<EmailTemplateDto> ordered by key. Requires permission **Settings.Read**.")]
    public async Task<Result<List<EmailTemplateDto>>> List(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetEmailTemplateListQuery(), ct);
        return Result<List<EmailTemplateDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{key}")]
    [Authorize(Policy = "Settings.Read")]
    [EndpointSummary("Email template by key")]
    [EndpointDescription(@"Single email template identified by its symbolic key.
Filters / params:
- **key**: Required — template key (e.g. ""SupplierInvite"", ""LoginOtp"").
Returns: EmailTemplateDto on success; 404 if key not found. Requires permission **Settings.Read**.")]
    public async Task<Result<EmailTemplateDto>> GetByKey(string key, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetEmailTemplateByKeyQuery(key), ct);
        return Result<EmailTemplateDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("{key}")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Update email template")]
    [EndpointDescription(@"Updates the subject + body of an existing email template.
Filters / params:
- **key**: Required — template key.
- **body**: UpdateEmailTemplateRequest with new Subject + Body content.
Side effects:
- Overwrites the stored template; downstream mail sends will use the new copy immediately.
Returns: true on success; 404 if key not found. Requires permission **Settings.Write**.")]
    public async Task<Result<bool>> Update(
        string key, [FromBody] UpdateEmailTemplateRequest body, CancellationToken ct)
    {
        await _mediator.Send(new UpdateEmailTemplateCommand(key, body), ct);
        return Result<bool>.Ok(true, HttpContext.TraceIdentifier);
    }

    [HttpPost("test-send")]
    [Authorize(Policy = "Settings.Write")]
    [EndpointSummary("Send test email")]
    [EndpointDescription(@"Renders a template with sample tokens and dispatches it to a test recipient.
Body:
- **body**: SendTestEmailTemplateRequest with template key + recipient email.
Side effects:
- Sends a real email through the configured SMTP provider.
Returns: TestEmailResult with delivery status + provider response. Requires permission **Settings.Write**.")]
    public async Task<Result<TestEmailResult>> TestSend(
        [FromBody] SendTestEmailTemplateRequest body, CancellationToken ct)
    {
        var result = await _mediator.Send(new SendTestEmailTemplateCommand(body), ct);
        return Result<TestEmailResult>.Ok(result, HttpContext.TraceIdentifier);
    }
}
