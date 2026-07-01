using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Integration.Connection;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Tenant-scoped Infor CloudSuite (ION API) connection configuration, managed from the System Settings
/// section. Stored one row per tenant; secrets are encrypted at rest and masked on read. Guarded by the
/// Settings policies because it is surfaced and administered through the Settings UI.
/// </summary>
[ApiController]
[Authorize]
[Route("api/infor-connection")]
public class InforConnectionController : ControllerBase
{
    private readonly IMediator _mediator;
    public InforConnectionController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.SettingsRead)]
    [EndpointSummary("Get Infor connection config")]
    [EndpointDescription(@"Returns the current tenant's Infor CloudSuite (ION API) connection configuration.
Secrets (Client Secret, Password) are masked with ""********"" when set; ciphertext never leaves the API.
Returns: InforConnectionDto. Requires permission **Settings.Read**.")]
    public async Task<Result<InforConnectionDto>> Get(CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInforConnectionQuery(), ct);
        return Result<InforConnectionDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Save Infor connection config")]
    [EndpointDescription(@"Creates or updates the current tenant's Infor connection configuration (one row per tenant).
Body:
- **body**: SaveInforConnectionRequest. A secret equal to ""********"" leaves the stored value untouched.
Side effects:
- Secrets are encrypted via DataProtection before persistence.
Returns: true on success; 400 on validation. Requires permission **Settings.Write**.")]
    public async Task<Result<bool>> Save([FromBody] SaveInforConnectionRequest body, CancellationToken ct)
    {
        await _mediator.Send(new SaveInforConnectionCommand(body), ct);
        return Result<bool>.Ok(true, HttpContext.TraceIdentifier);
    }

    [HttpPost("test")]
    [Authorize(Policy = Perm.SettingsWrite)]
    [EndpointSummary("Test Infor connection")]
    [EndpointDescription(@"Requests an OAuth2 token from Infor Mingle SSO using the supplied settings to verify the connection.
Body:
- **body**: TestInforConnectionRequest. Secrets equal to ""********"" fall back to the stored (decrypted) values.
Side effects:
- Makes a live outbound call to the Infor token endpoint. No data is persisted.
Returns: InforConnectionTestResult with a success flag + diagnostic message. Requires permission **Settings.Write**.")]
    public async Task<Result<InforConnectionTestResult>> Test([FromBody] TestInforConnectionRequest body, CancellationToken ct)
    {
        var result = await _mediator.Send(new TestInforConnectionCommand(body), ct);
        return Result<InforConnectionTestResult>.Ok(result, HttpContext.TraceIdentifier);
    }
}
