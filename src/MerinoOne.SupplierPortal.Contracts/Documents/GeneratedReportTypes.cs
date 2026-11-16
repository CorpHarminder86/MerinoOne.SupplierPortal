namespace MerinoOne.SupplierPortal.Contracts.Documents;

/// <summary>
/// R19 (2026-11-16) — the report-type values stored on a generated (portal-rendered) document upload, so the
/// attachment panel / register can tell a Packing Slip PDF from an e-invoice PDF.
/// </summary>
public static class GeneratedReportTypes
{
    public const string PackingSlip = "PackingSlip";
    public const string EInvoice = "EInvoice";
}
