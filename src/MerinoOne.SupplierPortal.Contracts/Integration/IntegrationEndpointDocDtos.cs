namespace MerinoOne.SupplierPortal.Contracts.Integration;

/// <summary>
/// One documented external inbound integration endpoint, surfaced to the in-app developer-docs page
/// (/integrations/docs). <see cref="Scope"/> is the <c>Integration.Inbound.*</c> token a partner's API key
/// must carry to call it (selecting an endpoint adds this scope to the minted key). <see cref="CompanyScoped"/>
/// flags endpoints whose body carries a CompanyCode (so the key needs bound source companies).
/// </summary>
public record IntegrationEndpointDocDto(
    string DisplayName,
    string Scope,
    string Method,
    string Path,
    bool CompanyScoped,
    string Description,
    string SampleJson);
