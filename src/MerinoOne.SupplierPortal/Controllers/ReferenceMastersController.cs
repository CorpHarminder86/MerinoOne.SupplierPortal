using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Masters.Mdm;
using MerinoOne.SupplierPortal.Contracts.Masters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// CRUD + typeahead for the INFOR LN reference masters (Currency / Country / State / City / PostalCode /
/// Unit / ItemGroup). Shares the <c>api/masters</c> space with <see cref="MastersController"/>.
/// Read = <c>Settings.Read</c>, write = <c>Settings.Write</c>. Geo lookups back the supplier-address cascade.
/// </summary>
[ApiController]
[Authorize]
[Route("api/masters")]
public class ReferenceMastersController(IMediator mediator) : ControllerBase
{
    // ---------------- Currency ----------------
    [HttpGet("currencies"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<CurrencyDto>>> ListCurrencies([FromQuery] bool? isActive, [FromQuery] string? search, CancellationToken ct)
        => Result<List<CurrencyDto>>.Ok(await mediator.Send(new GetCurrenciesQuery(isActive, search), ct), HttpContext.TraceIdentifier);

    [HttpGet("currencies/{id:guid}"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<CurrencyDto>> GetCurrency(Guid id, CancellationToken ct)
        => Result<CurrencyDto>.Ok(await mediator.Send(new GetCurrencyByIdQuery(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("currencies"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<CurrencyDto>> CreateCurrency([FromBody] CreateCurrencyRequest body, CancellationToken ct)
        => Result<CurrencyDto>.Ok(await mediator.Send(new CreateCurrencyCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPut("currencies/{id:guid}"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<CurrencyDto>> UpdateCurrency(Guid id, [FromBody] UpdateCurrencyRequest body, CancellationToken ct)
        => Result<CurrencyDto>.Ok(await mediator.Send(new UpdateCurrencyCommand(id, body), ct), HttpContext.TraceIdentifier);

    [HttpPost("currencies/{id:guid}/deactivate"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result> DeactivateCurrency(Guid id, CancellationToken ct)
    { await mediator.Send(new DeactivateCurrencyCommand(id), ct); return Result.Ok(HttpContext.TraceIdentifier); }

    // ---------------- Country ----------------
    [HttpGet("countries"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<CountryDto>>> ListCountries([FromQuery] bool? isActive, [FromQuery] string? search, CancellationToken ct)
        => Result<List<CountryDto>>.Ok(await mediator.Send(new GetCountriesQuery(isActive, search), ct), HttpContext.TraceIdentifier);

    [HttpGet("countries/{id:guid}"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<CountryDto>> GetCountry(Guid id, CancellationToken ct)
        => Result<CountryDto>.Ok(await mediator.Send(new GetCountryByIdQuery(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("countries"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<CountryDto>> CreateCountry([FromBody] CreateCountryRequest body, CancellationToken ct)
        => Result<CountryDto>.Ok(await mediator.Send(new CreateCountryCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPut("countries/{id:guid}"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<CountryDto>> UpdateCountry(Guid id, [FromBody] UpdateCountryRequest body, CancellationToken ct)
        => Result<CountryDto>.Ok(await mediator.Send(new UpdateCountryCommand(id, body), ct), HttpContext.TraceIdentifier);

    [HttpPost("countries/{id:guid}/deactivate"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result> DeactivateCountry(Guid id, CancellationToken ct)
    { await mediator.Send(new DeactivateCountryCommand(id), ct); return Result.Ok(HttpContext.TraceIdentifier); }

    // ---------------- State ----------------
    [HttpGet("states"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<StateDto>>> ListStates([FromQuery] bool? isActive, [FromQuery] string? search, [FromQuery] Guid? countryId, CancellationToken ct)
        => Result<List<StateDto>>.Ok(await mediator.Send(new GetStatesQuery(isActive, search, countryId), ct), HttpContext.TraceIdentifier);

    [HttpGet("states/{id:guid}"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<StateDto>> GetState(Guid id, CancellationToken ct)
        => Result<StateDto>.Ok(await mediator.Send(new GetStateByIdQuery(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("states"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<StateDto>> CreateState([FromBody] CreateStateRequest body, CancellationToken ct)
        => Result<StateDto>.Ok(await mediator.Send(new CreateStateCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPut("states/{id:guid}"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<StateDto>> UpdateState(Guid id, [FromBody] UpdateStateRequest body, CancellationToken ct)
        => Result<StateDto>.Ok(await mediator.Send(new UpdateStateCommand(id, body), ct), HttpContext.TraceIdentifier);

    [HttpPost("states/{id:guid}/deactivate"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result> DeactivateState(Guid id, CancellationToken ct)
    { await mediator.Send(new DeactivateStateCommand(id), ct); return Result.Ok(HttpContext.TraceIdentifier); }

    // ---------------- City ----------------
    [HttpGet("cities"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<CityDto>>> ListCities([FromQuery] bool? isActive, [FromQuery] string? search, [FromQuery] Guid? countryId, [FromQuery] Guid? stateId, CancellationToken ct)
        => Result<List<CityDto>>.Ok(await mediator.Send(new GetCitiesQuery(isActive, search, countryId, stateId), ct), HttpContext.TraceIdentifier);

    [HttpGet("cities/{id:guid}"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<CityDto>> GetCity(Guid id, CancellationToken ct)
        => Result<CityDto>.Ok(await mediator.Send(new GetCityByIdQuery(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("cities"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<CityDto>> CreateCity([FromBody] CreateCityRequest body, CancellationToken ct)
        => Result<CityDto>.Ok(await mediator.Send(new CreateCityCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPut("cities/{id:guid}"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<CityDto>> UpdateCity(Guid id, [FromBody] UpdateCityRequest body, CancellationToken ct)
        => Result<CityDto>.Ok(await mediator.Send(new UpdateCityCommand(id, body), ct), HttpContext.TraceIdentifier);

    [HttpPost("cities/{id:guid}/deactivate"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result> DeactivateCity(Guid id, CancellationToken ct)
    { await mediator.Send(new DeactivateCityCommand(id), ct); return Result.Ok(HttpContext.TraceIdentifier); }

    // ---------------- PostalCode ----------------
    [HttpGet("postal-codes"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<PostalCodeDto>>> ListPostalCodes([FromQuery] bool? isActive, [FromQuery] string? search, [FromQuery] Guid? countryId, [FromQuery] Guid? stateId, [FromQuery] Guid? cityId, CancellationToken ct)
        => Result<List<PostalCodeDto>>.Ok(await mediator.Send(new GetPostalCodesQuery(isActive, search, countryId, stateId, cityId), ct), HttpContext.TraceIdentifier);

    [HttpGet("postal-codes/{id:guid}"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<PostalCodeDto>> GetPostalCode(Guid id, CancellationToken ct)
        => Result<PostalCodeDto>.Ok(await mediator.Send(new GetPostalCodeByIdQuery(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("postal-codes"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<PostalCodeDto>> CreatePostalCode([FromBody] CreatePostalCodeRequest body, CancellationToken ct)
        => Result<PostalCodeDto>.Ok(await mediator.Send(new CreatePostalCodeCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPut("postal-codes/{id:guid}"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<PostalCodeDto>> UpdatePostalCode(Guid id, [FromBody] UpdatePostalCodeRequest body, CancellationToken ct)
        => Result<PostalCodeDto>.Ok(await mediator.Send(new UpdatePostalCodeCommand(id, body), ct), HttpContext.TraceIdentifier);

    [HttpPost("postal-codes/{id:guid}/deactivate"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result> DeactivatePostalCode(Guid id, CancellationToken ct)
    { await mediator.Send(new DeactivatePostalCodeCommand(id), ct); return Result.Ok(HttpContext.TraceIdentifier); }

    // ---------------- Unit ----------------
    [HttpGet("units"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<UnitDto>>> ListUnits([FromQuery] bool? isActive, [FromQuery] string? search, CancellationToken ct)
        => Result<List<UnitDto>>.Ok(await mediator.Send(new GetUnitsQuery(isActive, search), ct), HttpContext.TraceIdentifier);

    [HttpGet("units/{id:guid}"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<UnitDto>> GetUnit(Guid id, CancellationToken ct)
        => Result<UnitDto>.Ok(await mediator.Send(new GetUnitByIdQuery(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("units"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<UnitDto>> CreateUnit([FromBody] CreateUnitRequest body, CancellationToken ct)
        => Result<UnitDto>.Ok(await mediator.Send(new CreateUnitCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPut("units/{id:guid}"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<UnitDto>> UpdateUnit(Guid id, [FromBody] UpdateUnitRequest body, CancellationToken ct)
        => Result<UnitDto>.Ok(await mediator.Send(new UpdateUnitCommand(id, body), ct), HttpContext.TraceIdentifier);

    [HttpPost("units/{id:guid}/deactivate"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result> DeactivateUnit(Guid id, CancellationToken ct)
    { await mediator.Send(new DeactivateUnitCommand(id), ct); return Result.Ok(HttpContext.TraceIdentifier); }

    // ---------------- ItemGroup ----------------
    [HttpGet("item-groups"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<ItemGroupDto>>> ListItemGroups([FromQuery] bool? isActive, [FromQuery] string? search, CancellationToken ct)
        => Result<List<ItemGroupDto>>.Ok(await mediator.Send(new GetItemGroupsQuery(isActive, search), ct), HttpContext.TraceIdentifier);

    [HttpGet("item-groups/{id:guid}"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<ItemGroupDto>> GetItemGroup(Guid id, CancellationToken ct)
        => Result<ItemGroupDto>.Ok(await mediator.Send(new GetItemGroupByIdQuery(id), ct), HttpContext.TraceIdentifier);

    [HttpPost("item-groups"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<ItemGroupDto>> CreateItemGroup([FromBody] CreateItemGroupRequest body, CancellationToken ct)
        => Result<ItemGroupDto>.Ok(await mediator.Send(new CreateItemGroupCommand(body), ct), HttpContext.TraceIdentifier);

    [HttpPut("item-groups/{id:guid}"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result<ItemGroupDto>> UpdateItemGroup(Guid id, [FromBody] UpdateItemGroupRequest body, CancellationToken ct)
        => Result<ItemGroupDto>.Ok(await mediator.Send(new UpdateItemGroupCommand(id, body), ct), HttpContext.TraceIdentifier);

    [HttpPost("item-groups/{id:guid}/deactivate"), Authorize(Policy = Perm.SettingsWrite)]
    public async Task<Result> DeactivateItemGroup(Guid id, CancellationToken ct)
    { await mediator.Send(new DeactivateItemGroupCommand(id), ct); return Result.Ok(HttpContext.TraceIdentifier); }

    // ---------------- Geo lookups (authenticated typeahead for the address cascade) ----------------
    [HttpGet("geo/countries"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<GeoLookupDto>>> GeoCountries([FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
        => Result<List<GeoLookupDto>>.Ok(await mediator.Send(new GeoCountryLookupQuery(search, null, skip), ct), HttpContext.TraceIdentifier);

    [HttpGet("geo/states"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<GeoLookupDto>>> GeoStates([FromQuery] Guid countryId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
        => Result<List<GeoLookupDto>>.Ok(await mediator.Send(new GeoStateLookupQuery(countryId, search, null, skip), ct), HttpContext.TraceIdentifier);

    [HttpGet("geo/cities"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<GeoLookupDto>>> GeoCities([FromQuery] Guid? countryId, [FromQuery] Guid? stateId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
        => Result<List<GeoLookupDto>>.Ok(await mediator.Send(new GeoCityLookupQuery(countryId, stateId, search, null, skip), ct), HttpContext.TraceIdentifier);

    [HttpGet("geo/postal-codes"), Authorize(Policy = Perm.SettingsRead)]
    public async Task<Result<List<GeoLookupDto>>> GeoPostalCodes([FromQuery] Guid? countryId, [FromQuery] Guid? stateId, [FromQuery] Guid? cityId, [FromQuery] string? search, [FromQuery] int skip, CancellationToken ct)
        => Result<List<GeoLookupDto>>.Ok(await mediator.Send(new GeoPostalCodeLookupQuery(countryId, stateId, cityId, search, null, skip), ct), HttpContext.TraceIdentifier);
}
