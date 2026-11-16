using MerinoOne.SupplierPortal.Contracts.Invoices;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// R19 (2026-11-16) — renders an invoice's supplier-format report read model (<see cref="SupplierInvoiceReportDto"/> — every
/// value already resolved and totalled by <c>GetSupplierInvoiceReportQuery</c>, so the renderer does no lookups) to an A4 PDF
/// byte stream. Implemented in Infrastructure (QuestPDF, Community license set at startup).
/// </summary>
public interface ISupplierInvoicePdfGenerator
{
    byte[] Generate(SupplierInvoiceReportDto dto);
}
