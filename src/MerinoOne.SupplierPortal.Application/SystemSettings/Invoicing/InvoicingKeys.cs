namespace MerinoOne.SupplierPortal.Application.SystemSettings.Invoicing;

/// <summary>
/// R6 (2026-07-02) — plan D11. Tenant-wide invoicing-control settings category (e-invoice compliance gates
/// enforced at invoice Submit).
/// </summary>
public static class InvoicingKeys
{
    public const string Category = "Invoicing";

    /// <summary>When "true", invoice Submit REQUIRES a non-blank e-invoice IRN. Default "false".</summary>
    public const string RequireIrn = "RequireIrn";

    /// <summary>When "true", invoice Submit REQUIRES a non-blank e-way bill number. Default "false".</summary>
    public const string RequireEWayBill = "RequireEWayBill";
}
