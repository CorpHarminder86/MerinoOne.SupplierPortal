using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.SystemSettings;
using MerinoOne.SupplierPortal.Application.SystemSettings.Commands;
using MerinoOne.SupplierPortal.Application.SystemSettings.Queries;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/system-settings")]
public class SystemSettingsController : ControllerBase
{
    private readonly IMediator _mediator;
    public SystemSettingsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = "Settings.Read")]
    public async Task<Result<List<SystemSettingDto>>> List([FromQuery] string category, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetSystemSettingsQuery(category ?? string.Empty), ct);
        return Result<List<SystemSettingDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<bool>> Save([FromBody] SaveSystemSettingRequest body, CancellationToken ct)
    {
        await _mediator.Send(new SaveSystemSettingCommand(body), ct);
        return Result<bool>.Ok(true, HttpContext.TraceIdentifier);
    }

    [HttpPost("reset")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<bool>> Reset([FromBody] ResetSystemSettingRequest body, CancellationToken ct)
    {
        await _mediator.Send(new ResetSystemSettingCommand(body), ct);
        return Result<bool>.Ok(true, HttpContext.TraceIdentifier);
    }

    [HttpPost("email/test")]
    [Authorize(Policy = "Settings.Write")]
    public async Task<Result<TestEmailResult>> SendTestEmail(
        [FromBody] SendTestEmailRequest body, CancellationToken ct)
    {
        var result = await _mediator.Send(new SendTestEmailCommand(body?.ToEmail), ct);
        return Result<TestEmailResult>.Ok(result, HttpContext.TraceIdentifier);
    }
}
