namespace MerinoOne.SupplierPortal.Contracts.Integration;

// ============================================================================================
// Inbound master records pushed by Infor LN. Parent references are CODES (resolved to Guids
// server-side, within the row's scope). Tenant-scoped masters (Currency/Country/State/City/
// PostalCode) push bodies carry NO CompanyCode — the tenant comes from the API key. Company-scoped
// masters (Unit/ItemGroup/Item) carry CompanyCode (resolved + normalized to the share-group source).
// Reuse UpsertResultDto / RowResult / RowOutcome from InboundTermDtos.
// ============================================================================================

// ---- Records ----
public record CurrencyRecord(string Code, string Description, string? IsoCode, string? Symbol, int DecimalPlaces = 2, bool IsActive = true);
public record CountryRecord(string Code, string Description, string? IsoCode2, string? IsoCode3, string? TelephoneCode, string? CurrencyCode, bool IsActive = true);
public record StateRecord(string Code, string Description, string CountryCode, string? IsoCode, bool IsActive = true);
public record CityRecord(string Code, string Description, string CountryCode, string? StateCode, bool IsActive = true);
public record PostalCodeRecord(string Code, string? Area, string CountryCode, string? StateCode, string? CityCode, bool IsActive = true);
public record UnitRecord(string Code, string Description, string UnitType, string? IsoCode, int DecimalPlaces, decimal ConversionFactor, string? BaseUnitCode, bool IsActive = true);
public record ItemGroupRecord(string Code, string Description, bool IsActive = true);
public record ItemRecord(string Code, string Description, string? UnitCode, string? ItemGroupCode, string? HsnCode, bool IsActive = true);
public record TaxRecord(string Code, string Description, decimal? TaxRate = null, bool IsActive = true);

// ---- Tenant-scoped push bodies (no CompanyCode) ----
public record PushCurrenciesRequest(IReadOnlyList<CurrencyRecord> Records);
public record PushCountriesRequest(IReadOnlyList<CountryRecord> Records);
public record PushStatesRequest(IReadOnlyList<StateRecord> Records);
public record PushCitiesRequest(IReadOnlyList<CityRecord> Records);
public record PushPostalCodesRequest(IReadOnlyList<PostalCodeRecord> Records);

// ---- Company-scoped push bodies (CompanyCode) ----
public record PushUnitsRequest(string CompanyCode, IReadOnlyList<UnitRecord> Units);
public record PushItemGroupsRequest(string CompanyCode, IReadOnlyList<ItemGroupRecord> ItemGroups);
public record PushItemsRequest(string CompanyCode, IReadOnlyList<ItemRecord> Items);
public record PushTaxesRequest(string CompanyCode, IReadOnlyList<TaxRecord> Taxes);
