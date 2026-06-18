namespace MerinoOne.SupplierPortal.Contracts.Masters;

// ============================================================================================
// Reference master DTOs (Currency / Country / State / City / PostalCode / Unit / ItemGroup).
// Internal CRUD requests carry Guid parent ids (dropdowns); read DTOs add parent code/name for grids.
// UnitType is the enum NAME (string) so Contracts stays free of a Domain reference.
// ============================================================================================

/// <summary>Slim typeahead projection shared by the geo lookup endpoints.</summary>
public record GeoLookupDto(Guid Id, string Code, string Name);

/// <summary>
/// Full geo detail for a single postal code, used by the registration address step to reverse-cascade
/// autofill area/city/state/country when the supplier selects a PIN code. Linked geo names are the
/// master Descriptions; ids let the UI snap the dependent dropdowns to the resolved rows.
/// </summary>
public record PostalCodeDetailDto(
    Guid Id,
    string Code,
    string? Area,
    Guid? CityId,
    string? CityName,
    Guid? StateId,
    string? StateName,
    Guid? CountryId,
    string? CountryName);

// ---- Currency ----
public record CurrencyDto(Guid Id, int Seq, string Code, string Description, string? IsoCode, string? Symbol, int DecimalPlaces, bool IsActive, DateTime CreatedOn);
public record CreateCurrencyRequest(string Code, string Description, string? IsoCode, string? Symbol, int DecimalPlaces = 2, bool IsActive = true);
public record UpdateCurrencyRequest(string Description, string? IsoCode, string? Symbol, int DecimalPlaces, bool IsActive);

// ---- Country ----
public record CountryDto(Guid Id, int Seq, string Code, string Description, string? IsoCode2, string? IsoCode3, string? TelephoneCode, Guid? CurrencyId, string? CurrencyCode, bool IsActive, DateTime CreatedOn);
public record CreateCountryRequest(string Code, string Description, string? IsoCode2, string? IsoCode3, string? TelephoneCode, Guid? CurrencyId, bool IsActive = true);
public record UpdateCountryRequest(string Description, string? IsoCode2, string? IsoCode3, string? TelephoneCode, Guid? CurrencyId, bool IsActive);

// ---- State ----
public record StateDto(Guid Id, int Seq, string Code, string Description, Guid CountryId, string? CountryCode, string? CountryName, string? IsoCode, bool IsActive, DateTime CreatedOn);
public record CreateStateRequest(string Code, string Description, Guid CountryId, string? IsoCode, bool IsActive = true);
public record UpdateStateRequest(string Description, Guid CountryId, string? IsoCode, bool IsActive);

// ---- City ----
public record CityDto(Guid Id, int Seq, string Code, string Description, Guid CountryId, string? CountryName, Guid? StateId, string? StateName, bool IsActive, DateTime CreatedOn);
public record CreateCityRequest(string Code, string Description, Guid CountryId, Guid? StateId, bool IsActive = true);
public record UpdateCityRequest(string Description, Guid CountryId, Guid? StateId, bool IsActive);

// ---- PostalCode ----
public record PostalCodeDto(Guid Id, int Seq, string Code, string? Area, Guid CountryId, string? CountryName, Guid? StateId, string? StateName, Guid? CityId, string? CityName, bool IsActive, DateTime CreatedOn);
public record CreatePostalCodeRequest(string Code, string? Area, Guid CountryId, Guid? StateId, Guid? CityId, bool IsActive = true);
public record UpdatePostalCodeRequest(string? Area, Guid CountryId, Guid? StateId, Guid? CityId, bool IsActive);

// ---- Unit ----
public record UnitDto(Guid Id, int Seq, string Code, string Description, string UnitType, string? IsoCode, int DecimalPlaces, decimal ConversionFactor, Guid? BaseUnitId, string? BaseUnitCode, bool IsActive, DateTime CreatedOn);
public record CreateUnitRequest(string Code, string Description, string UnitType, string? IsoCode, int DecimalPlaces, decimal ConversionFactor, Guid? BaseUnitId, bool IsActive = true);
public record UpdateUnitRequest(string Description, string UnitType, string? IsoCode, int DecimalPlaces, decimal ConversionFactor, Guid? BaseUnitId, bool IsActive);

// ---- ItemGroup ----
public record ItemGroupDto(Guid Id, int Seq, string Code, string Description, bool IsActive, DateTime CreatedOn);
public record CreateItemGroupRequest(string Code, string Description, bool IsActive = true);
public record UpdateItemGroupRequest(string Description, bool IsActive);
