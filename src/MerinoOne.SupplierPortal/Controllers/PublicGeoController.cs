using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.Mdm;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Anonymous geo typeahead for the supplier onboarding address step. Scoped to the inviting tenant — the
/// <c>token</c> (the registration invite token) is resolved to a tenant and rejected when unknown/expired/
/// consumed. Hardened: a supplied <c>search</c> must be ≥2 chars, results are capped (server-side), and the
/// route group is rate-limited (token+IP partition).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public/geo")]
[EnableRateLimiting("public-geo")]
public class PublicGeoController(IMediator mediator) : ControllerBase
{
    private const int MinSearch = 2;

    [HttpGet("countries")]
    public async Task<Result<List<GeoLookupDto>>> Countries([FromQuery] string token, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
    {
        if (TooShort(search)) return Empty();
        var tenantId = await mediator.Send(new GetInviteTenantQuery(token), ct);
        return Ok(await mediator.Send(new GeoCountryLookupQuery(search, tenantId, skip), ct));
    }

    [HttpGet("states")]
    public async Task<Result<List<GeoLookupDto>>> States([FromQuery] string token, [FromQuery] Guid countryId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
    {
        if (TooShort(search)) return Empty();
        var tenantId = await mediator.Send(new GetInviteTenantQuery(token), ct);
        return Ok(await mediator.Send(new GeoStateLookupQuery(countryId, search, tenantId, skip), ct));
    }

    [HttpGet("cities")]
    public async Task<Result<List<GeoLookupDto>>> Cities([FromQuery] string token, [FromQuery] Guid? countryId, [FromQuery] Guid? stateId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
    {
        if (TooShort(search)) return Empty();
        var tenantId = await mediator.Send(new GetInviteTenantQuery(token), ct);
        return Ok(await mediator.Send(new GeoCityLookupQuery(countryId, stateId, search, tenantId, skip), ct));
    }

    [HttpGet("postal-codes")]
    public async Task<Result<List<GeoLookupDto>>> PostalCodes([FromQuery] string token, [FromQuery] Guid? countryId, [FromQuery] Guid? stateId, [FromQuery] Guid? cityId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
    {
        if (TooShort(search)) return Empty();
        var tenantId = await mediator.Send(new GetInviteTenantQuery(token), ct);
        return Ok(await mediator.Send(new GeoPostalCodeLookupQuery(countryId, stateId, cityId, search, tenantId, skip), ct));
    }

    /// <summary>
    /// Reverse-cascade autofill: full geo detail for a single PIN so the address step can fill
    /// area/city/state/country when a postal code is selected. Token-validated + tenant-scoped via the
    /// query handler (same invite gate as the typeahead lookups).
    /// </summary>
    [HttpGet("postal-codes/{id:guid}/detail")]
    public async Task<Result<PostalCodeDetailDto?>> PostalCodeDetail([FromRoute] Guid id, [FromQuery] string token, CancellationToken ct)
    {
        var detail = await mediator.Send(new GetPublicPostalCodeDetailQuery(token, id), ct);
        return Result<PostalCodeDetailDto?>.Ok(detail, HttpContext.TraceIdentifier);
    }

    private static bool TooShort(string? s) => !string.IsNullOrEmpty(s) && s.Trim().Length < MinSearch;
    private Result<List<GeoLookupDto>> Ok(List<GeoLookupDto> data) => Result<List<GeoLookupDto>>.Ok(data, HttpContext.TraceIdentifier);
    private Result<List<GeoLookupDto>> Empty() => Result<List<GeoLookupDto>>.Ok(new List<GeoLookupDto>(), HttpContext.TraceIdentifier);
}
