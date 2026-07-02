namespace MerinoOne.SupplierPortal.Application.SystemSettings.Invoicing;

/// <summary>
/// R6 (2026-07-02) — plan D11. Strongly-typed reader for the Invoicing settings category (invoice-submit
/// e-invoice compliance gates). Both default to <c>false</c> when unset/invalid.
/// </summary>
public interface IInvoicingSettings
{
    /// <summary>When <c>true</c>, invoice Submit requires a non-blank e-invoice IRN.</summary>
    bool RequireIrn { get; }

    /// <summary>When <c>true</c>, invoice Submit requires a non-blank e-way bill number.</summary>
    bool RequireEWayBill { get; }
}
