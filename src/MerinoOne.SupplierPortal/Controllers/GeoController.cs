using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.Mdm;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Authenticated geo typeahead for in-app address autocomplete (e.g. the supplier change-request editor). Mirrors
/// <see cref="PublicGeoController"/> but the tenant comes from the signed-in principal (the ambient query filter)
/// — no invite token. Reuses the same Geo*LookupQuery handlers (TenantId=null → ambient tenant scoping).
/// </summary>
[ApiController]
[Authorize]
[Route("api/geo")]
public class GeoController(IMediator mediator) : ControllerBase
{
    private const int MinSearch = 2;

    [HttpGet("countries")]
    public async Task<Result<List<GeoLookupDto>>> Countries([FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
        => TooShort(search) ? Empty() : Ok(await mediator.Send(new GeoCountryLookupQuery(search, null, skip), ct));

    [HttpGet("states")]
    public async Task<Result<List<GeoLookupDto>>> States([FromQuery] Guid countryId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
        => TooShort(search) ? Empty() : Ok(await mediator.Send(new GeoStateLookupQuery(countryId, search, null, skip), ct));

    [HttpGet("cities")]
    public async Task<Result<List<GeoLookupDto>>> Cities([FromQuery] Guid? countryId, [FromQuery] Guid? stateId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
        => TooShort(search) ? Empty() : Ok(await mediator.Send(new GeoCityLookupQuery(countryId, stateId, search, null, skip), ct));

    [HttpGet("postal-codes")]
    public async Task<Result<List<GeoLookupDto>>> PostalCodes([FromQuery] Guid? countryId, [FromQuery] Guid? stateId, [FromQuery] Guid? cityId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
        => TooShort(search) ? Empty() : Ok(await mediator.Send(new GeoPostalCodeLookupQuery(countryId, stateId, cityId, search, null, skip), ct));

    [HttpGet("postal-codes/{id:guid}/detail")]
    public async Task<Result<PostalCodeDetailDto?>> PostalCodeDetail([FromRoute] Guid id, CancellationToken ct)
        => Result<PostalCodeDetailDto?>.Ok(await mediator.Send(new GetPostalCodeDetailQuery(id), ct), HttpContext.TraceIdentifier);

    private static bool TooShort(string? s) => !string.IsNullOrEmpty(s) && s.Trim().Length < MinSearch;
    private Result<List<GeoLookupDto>> Ok(List<GeoLookupDto> data) => Result<List<GeoLookupDto>>.Ok(data, HttpContext.TraceIdentifier);
    private Result<List<GeoLookupDto>> Empty() => Result<List<GeoLookupDto>>.Ok(new List<GeoLookupDto>(), HttpContext.TraceIdentifier);
}
