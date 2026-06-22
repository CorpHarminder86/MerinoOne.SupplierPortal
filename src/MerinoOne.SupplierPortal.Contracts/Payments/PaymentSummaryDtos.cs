namespace MerinoOne.SupplierPortal.Contracts.Payments;

/// <summary>
/// Enhancement R4 — Module 7 (Payment Summary). One row per invoice, with the 10 spec columns.
/// Read-only projection over Invoice ⨝ Payment ⨝ GoodsReceipt ⨝ PaymentTerm.
/// </summary>
/// <param name="InvoiceNumber">Invoice.InvoiceNumber.</param>
/// <param name="InvoiceDate">Invoice.InvoiceDate.</param>
/// <param name="InvoiceAmount">Invoice.NetAmount (the net-of-tax payable amount).</param>
/// <param name="GrnNumber">GrnNumber of the latest-approved covering GRN (null if none approved yet).</param>
/// <param name="GrnDate">GrnDate/GrnApprovedAt of the latest-approved covering GRN.</param>
/// <param name="GrnCount">Count of GRNs linked to the invoice (drives the "+N" badge in the UI).</param>
/// <param name="IssueReported">Any non-empty GoodsReceipt.IssueReported across the covering GRNs (representative value).</param>
/// <param name="PaymentDueDate">Invoice.InvoiceDate + PaymentTerm.NetDays (PO term, fall back to supplier term).</param>
/// <param name="PaymentReference">PaymentReference of the latest payment (null if none received).</param>
/// <param name="ReceivedAmount">Σ Payment.NetPaid for the invoice.</param>
/// <param name="BalanceToReceive">Invoice.NetAmount − ReceivedAmount.</param>
public record PaymentSummaryRowDto(
    string InvoiceNumber,
    DateTime InvoiceDate,
    decimal InvoiceAmount,
    string? GrnNumber,
    DateTime? GrnDate,
    int GrnCount,
    string? IssueReported,
    DateTime? PaymentDueDate,
    string? PaymentReference,
    decimal ReceivedAmount,
    decimal BalanceToReceive);
