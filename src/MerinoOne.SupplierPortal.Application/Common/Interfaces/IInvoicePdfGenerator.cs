using MerinoOne.SupplierPortal.Contracts.Invoices;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// R6 (2026-07-02) — renders an invoice's FROZEN snapshot (the detail DTO as persisted — no live joins, no
/// recomputation) to a PDF byte stream. Implemented in Infrastructure (QuestPDF, Community license set at
/// startup); consumed by <c>GET api/invoices/{id}/pdf</c>.
/// </summary>
public interface IInvoicePdfGenerator
{
    byte[] Generate(InvoiceDetailDto invoice);
}
