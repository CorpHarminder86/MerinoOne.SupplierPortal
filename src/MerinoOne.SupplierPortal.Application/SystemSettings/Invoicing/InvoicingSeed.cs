namespace MerinoOne.SupplierPortal.Application.SystemSettings.Invoicing;

/// <summary>
/// R6 (2026-07-02) — plan D11. Declares the Invoicing settings category (keys / defaults / descriptions /
/// validators). Registered as <c>ISettingsCategorySeed</c> so the generic Get/Save/Reset pipeline and the
/// settings UI pick it up with no edits; the typed reader is <see cref="InvoicingSettingsService"/>.
/// </summary>
public class InvoicingSeed : ISettingsCategorySeed
{
    public string Category => InvoicingKeys.Category;

    public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // OFF by default (rollout control) — GST-regime tenants switch these on per policy.
        [InvoicingKeys.RequireIrn] = "false",
        [InvoicingKeys.RequireEWayBill] = "false",
    };

    public IReadOnlyDictionary<string, string?> Descriptions { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [InvoicingKeys.RequireIrn] =
            "When ON, submitting an invoice requires a non-blank e-invoice IRN (400 when missing).",
        [InvoicingKeys.RequireEWayBill] =
            "When ON, submitting an invoice requires a non-blank e-way bill number (400 when missing).",
    };

    public string? Validate(string key, string value)
    {
        switch (key)
        {
            case InvoicingKeys.RequireIrn:
                if (!bool.TryParse(value, out _))
                    return "RequireIrn must be 'true' or 'false'.";
                return null;

            case InvoicingKeys.RequireEWayBill:
                if (!bool.TryParse(value, out _))
                    return "RequireEWayBill must be 'true' or 'false'.";
                return null;

            default:
                return $"Unknown Invoicing key '{key}'.";
        }
    }
}
