using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Application.SystemSettings.Commands;
using MerinoOne.SupplierPortal.Application.SystemSettings.Queries;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/system-settings")]
public class SystemSettingsController : ControllerBase
{
    private readonly IMediator _mediator;
    public SystemSettingsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("List system settings")]
    [EndpointDescription(@"Returns system settings for a given category.
Filters / params:
- **category**: Required — settings category code (e.g. ""Email"", ""Integration"", ""Branding""). Empty returns all categories.
Returns: List<SystemSettingDto> with current values and defaults. Requires permission **Settings.Read**.")]
    public async Task<Result<List<SystemSettingDto>>> List([FromQuery] string category, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSystemSettingsQuery(category ?? string.Empty), ct);
        return Result<List<SystemSettingDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Save system setting")]
    [EndpointDescription(@"Persists a new or updated system-setting value.
Body:
- **body**: SaveSystemSettingRequest with category + key + value (and optional metadata).
Side effects:
- Writes to AuditTrail via the audit interceptor.
- Setting becomes effective immediately on next read.
Returns: true on success; 400 on validation. Requires permission **Settings.Write**.")]
    public async Task<Result<bool>> Save([FromBody] SaveSystemSettingRequest body, CancellationToken ct)
    {
        await _mediator.Send(new SaveSystemSettingCommand(body), ct);
        return Result<bool>.Ok(true, HttpContext.TraceIdentifier);
    }

    [HttpPost("reset")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Reset system setting")]
    [EndpointDescription(@"Reverts a system setting back to its built-in default value.
Body:
- **body**: ResetSystemSettingRequest identifying category + key to reset.
Side effects:
- Deletes the override row; subsequent reads return the seeded default.
- Writes to AuditTrail.
Returns: true on success; 404 if no override exists. Requires permission **Settings.Write**.")]
    public async Task<Result<bool>> Reset([FromBody] ResetSystemSettingRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ResetSystemSettingCommand(body), ct);
        return Result<bool>.Ok(true, HttpContext.TraceIdentifier);
    }

    [HttpPost("email/test")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Send test email")]
    [EndpointDescription(@"Sends a one-off test email using the currently configured SMTP/email settings.
Body:
- **body**: SendTestEmailRequest with the destination address.
Side effects:
- Dispatches a single test email via the live IEmailService.
Returns: TestEmailResult with success flag + provider response. Requires permission **Settings.Write**.")]
    public async Task<Result<TestEmailResult>> SendTestEmail(
        [FromBody] SendTestEmailRequest body, CancellationToken ct)
    {
        var result = await _mediator.Send(new SendTestEmailCommand(body?.ToEmail), ct);
        return Result<TestEmailResult>.Ok(result, HttpContext.TraceIdentifier);
    }
}
