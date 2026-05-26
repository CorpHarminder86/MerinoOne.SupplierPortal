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
    [EndpointSummary("List supplier invites")]
    [EndpointDescription(@"Lists supplier registration invites issued by the buyer organisation.
Filters / params:
- **status**: Optional — invite lifecycle status (Pending / Verified / Registered / Expired).
- **search**: Optional — free-text on invitee email / company name.
Returns: List<SupplierInviteListDto>. Requires permission **Supplier.Invite**.")]
    public async Task<Result<List<SupplierInviteListDto>>> ListInvites(
        [FromQuery] string? status, [FromQuery] string? search, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInvitesListQuery(status, search), ct);
        return Result<List<SupplierInviteListDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("invites")]
    [Authorize(Policy = "Supplier.Invite")]
    [EndpointSummary("Create supplier invite")]
    [EndpointDescription(@"Issues a new supplier registration invite + token.
Body:
- **body**: CreateSupplierInviteRequest with invitee email, company hint, contact name.
Side effects:
- Sends an OTP-protected registration email with a tokenised link.
- Persists the invite in Pending status.
Returns: CreateSupplierInviteResponse (invite + token + absolute registration URL). Requires permission **Supplier.Invite**.")]
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
    [EndpointSummary("Get invite by token")]
    [EndpointDescription(@"Looks up an invite by its registration token (anonymous, used by the registration page).
Filters / params:
- **token**: Required — invite token from the registration link.
Returns: SupplierInviteDetailDto on success; 404 if not found; 410 if expired. Anonymous endpoint.")]
    public async Task<Result<SupplierInviteDetailDto>> GetInviteByToken(string token, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInviteByTokenQuery(token), ct);
        return Result<SupplierInviteDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EndpointSummary("Register supplier")]
    [EndpointDescription(@"Completes supplier self-registration against an OTP-verified invite (anonymous).
Body:
- **body**: SupplierRegistrationRequest with full company profile + admin user details + invite token.
Side effects:
- Provisions the supplier record, the initial admin user, and the supplier-user mapping in one transaction.
- Marks the originating invite as Registered.
Returns: SupplierRegistrationResponse with supplier + admin credentials handoff; 400 on validation; 409 if invite already used. Anonymous endpoint.")]
    public async Task<Result<SupplierRegistrationResponse>> Register(
        [FromBody] SupplierRegistrationRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new RegisterSupplierCommand(body), ct);
        return Result<SupplierRegistrationResponse>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("invites/{token}/verify-otp")]
    [AllowAnonymous]
    [EndpointSummary("Verify invite OTP")]
    [EndpointDescription(@"Verifies the OTP a prospective supplier received via email (anonymous).
Filters / params:
- **token**: Required — invite token from the registration link.
Body:
- **body**: VerifyInviteOtpRequest with the 6-digit code.
Side effects:
- On success unlocks the registration form for this invite.
- Tracks attempt count and locks out after too many failures.
Returns: VerifyInviteOtpResponse; 400 on invalid code; 410 if OTP expired. Anonymous endpoint.")]
    public async Task<Result<VerifyInviteOtpResponse>> VerifyInviteOtp(
        string token, [FromBody] VerifyInviteOtpRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new VerifyInviteOtpCommand(token, body.Code), ct);
        return Result<VerifyInviteOtpResponse>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("invites/{token}/resend-otp")]
    [AllowAnonymous]
    [EndpointSummary("Resend invite OTP")]
    [EndpointDescription(@"Requests a fresh OTP for a pending supplier invite (anonymous).
Filters / params:
- **token**: Required — invite token from the registration link.
Side effects:
- Invalidates the previous OTP and emails a new one to the invited address.
- Rate-limited to prevent abuse.
Returns: ResendInviteOtpResponse; 404 if invite not found; 429 if requested too soon. Anonymous endpoint.")]
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
