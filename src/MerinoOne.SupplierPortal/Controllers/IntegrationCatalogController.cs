using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Contracts.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// Read-only catalog of the external inbound integration endpoints, consumed by the in-app developer-docs
/// page (/integrations/docs). Kept separate from <see cref="InboundIntegrationController"/> (which is the
/// external, X-APIKey-authed surface) so this stays JWT/permission-gated. The interactive try-it reference is
/// the filtered Scalar at /integration-docs.
/// </summary>
[ApiController]
[Authorize]
[Route("api/integration")]
public class IntegrationCatalogController : ControllerBase
{
    [HttpGet("catalog")]
    [Authorize(Policy = "Integration.Read")]
    public Result<List<IntegrationEndpointDocDto>> Catalog()
        => Result<List<IntegrationEndpointDocDto>>.Ok(IntegrationCatalog.All.ToList(), HttpContext.TraceIdentifier);
}

/// <summary>
/// Single source of truth for the partner-facing inbound endpoint docs. Each entry's <c>Scope</c> MUST be a
/// member of <c>ApiKeyScopes.Allowed</c> — a Development startup guard in Program.cs asserts the two sets are
/// equal, so adding an 11th scoped endpoint (or removing one) without updating this catalog fails fast.
/// </summary>
public static class IntegrationCatalog
{
    private const string Base = "api/integration/inbound";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string Json(object sample) => JsonSerializer.Serialize(sample, JsonOpts);

    public static readonly IReadOnlyList<IntegrationEndpointDocDto> All = new[]
    {
        // ── Company-scoped (body carries CompanyCode; key needs bound source companies) ──
        new IntegrationEndpointDocDto("Payment terms", "Integration.Inbound.PaymentTerm", "POST", $"{Base}/payment-terms", true,
            "Upsert Payment Term master rows for the resolved source company.",
            Json(new { companyCode = "3000", terms = new[] { new { code = "N30", description = "Net 30 days", netDays = 30, isActive = true } } })),
        new IntegrationEndpointDocDto("Delivery terms", "Integration.Inbound.DeliveryTerm", "POST", $"{Base}/delivery-terms", true,
            "Upsert Delivery Term master rows for the resolved source company.",
            Json(new { companyCode = "3000", terms = new[] { new { code = "FOB", description = "Free on board", isActive = true } } })),
        new IntegrationEndpointDocDto("Units", "Integration.Inbound.Unit", "POST", $"{Base}/units", true,
            "Upsert unit-of-measure master rows for the resolved source company.",
            Json(new { companyCode = "3000", units = new[] { new { code = "KG", description = "Kilogram", unitType = "Mass", isoCode = "KGM", decimalPlaces = 3, conversionFactor = 1, baseUnitCode = (string?)null, isActive = true } } })),
        new IntegrationEndpointDocDto("Item groups", "Integration.Inbound.ItemGroup", "POST", $"{Base}/item-groups", true,
            "Upsert item-group master rows for the resolved source company.",
            Json(new { companyCode = "3000", itemGroups = new[] { new { code = "RAW", description = "Raw materials", isActive = true } } })),
        new IntegrationEndpointDocDto("Items", "Integration.Inbound.Item", "POST", $"{Base}/items", true,
            "Upsert item master rows (unit + group referenced by code) for the resolved source company.",
            Json(new { companyCode = "3000", items = new[] { new { code = "ITM-00001", description = "Sample item", unitCode = "KG", itemGroupCode = "RAW", hsnCode = "39021000", isActive = true } } })),

        // ── Tenant-scoped (no CompanyCode; bound to the key's tenant) ──
        new IntegrationEndpointDocDto("Currencies", "Integration.Inbound.Currency", "POST", $"{Base}/currencies", false,
            "Upsert tenant-scoped Currency master rows (no company code).",
            Json(new { records = new[] { new { code = "USD", description = "US Dollar", isoCode = "USD", symbol = "$", decimalPlaces = 2, isActive = true } } })),
        new IntegrationEndpointDocDto("Countries", "Integration.Inbound.Country", "POST", $"{Base}/countries", false,
            "Upsert tenant-scoped Country master rows (currency referenced by code).",
            Json(new { records = new[] { new { code = "IN", description = "India", isoCode2 = "IN", isoCode3 = "IND", telephoneCode = "91", currencyCode = "INR", isActive = true } } })),
        new IntegrationEndpointDocDto("States", "Integration.Inbound.State", "POST", $"{Base}/states", false,
            "Upsert tenant-scoped State master rows (country referenced by code).",
            Json(new { records = new[] { new { code = "MH", description = "Maharashtra", countryCode = "IN", isoCode = "MH", isActive = true } } })),
        new IntegrationEndpointDocDto("Cities", "Integration.Inbound.City", "POST", $"{Base}/cities", false,
            "Upsert tenant-scoped City master rows (country/state referenced by code).",
            Json(new { records = new[] { new { code = "MH-MUM", description = "Mumbai", countryCode = "IN", stateCode = "MH", isActive = true } } })),
        new IntegrationEndpointDocDto("Postal codes", "Integration.Inbound.PostalCode", "POST", $"{Base}/postal-codes", false,
            "Upsert tenant-scoped Postal Code master rows (country/state/city referenced by code).",
            Json(new { records = new[] { new { code = "400001", area = "Fort", countryCode = "IN", stateCode = "MH", cityCode = "MH-MUM", isActive = true } } })),
    };
}
