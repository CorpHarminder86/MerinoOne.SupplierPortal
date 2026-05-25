using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.SupplierRegistration.Commands;
using MerinoOne.SupplierPortal.Application.SupplierRegistration.Queries;
using MerinoOne.SupplierPortal.Contracts.SupplierRegistration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/supplier-registration")]
public class SupplierRegistrationController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IConfiguration _config;
    public SupplierRegistrationController(IMediator mediator, IConfiguration config)
    {
        _mediator = mediator;
        _config = config;
    }

    [HttpGet("invites")]
    [Authorize(Policy = "Supplier.Invite")]
    public async Task<Result<List<SupplierInviteListDto>>> ListInvites(
        [FromQuery] string? status, [FromQuery] string? search, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInvitesListQuery(status, search), ct);
        return Result<List<SupplierInviteListDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("invites")]
    [Authorize(Policy = "Supplier.Invite")]
    public async Task<Result<CreateSupplierInviteResponse>> CreateInvite(
        [FromBody] CreateSupplierInviteRequest body, CancellationToken ct)
    {
        var invite = await _mediator.Send(new CreateSupplierInviteCommand(body), ct);
        var registrationUrl = BuildRegistrationUrl(invite.Token);
        var response = new CreateSupplierInviteResponse(invite, invite.Token, registrationUrl);
        return Result<CreateSupplierInviteResponse>.Ok(response, HttpContext.TraceIdentifier);
    }

    [HttpGet("invites/by-token/{token}")]
    [AllowAnonymous]
    public async Task<Result<SupplierInviteDetailDto>> GetInviteByToken(string token, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInviteByTokenQuery(token), ct);
        return Result<SupplierInviteDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<Result<SupplierRegistrationResponse>> Register(
        [FromBody] SupplierRegistrationRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new RegisterSupplierCommand(body), ct);
        return Result<SupplierRegistrationResponse>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("invites/{token}/verify-otp")]
    [AllowAnonymous]
    public async Task<Result<VerifyInviteOtpResponse>> VerifyInviteOtp(
        string token, [FromBody] VerifyInviteOtpRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new VerifyInviteOtpCommand(token, body.Code), ct);
        return Result<VerifyInviteOtpResponse>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("invites/{token}/resend-otp")]
    [AllowAnonymous]
    public async Task<Result<ResendInviteOtpResponse>> ResendInviteOtp(
        string token, CancellationToken ct)
    {
        var data = await _mediator.Send(new ResendInviteOtpCommand(token), ct);
        return Result<ResendInviteOtpResponse>.Ok(data, HttpContext.TraceIdentifier);
    }

    private string BuildRegistrationUrl(string token)
    {
        // Web URL is configured under Web:BaseUrl. Falls back to the request origin.
        var configured = _config["Web:BaseUrl"];
        var baseUrl = !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : $"{Request.Scheme}://{Request.Host}";
        return $"{baseUrl}/register/{token}";
    }
}
