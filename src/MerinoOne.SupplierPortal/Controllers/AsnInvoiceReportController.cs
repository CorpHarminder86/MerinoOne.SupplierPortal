using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Documents;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Contracts.Documents;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// 2026-11-17 feedback — the ASN's Supplier Invoice report BEFORE Post (the R6 draft invoice only exists after Post, yet Post
/// needs the supplier's invoice). Same availability rule and the same preview / download+auto-attach pair as the Packing List
/// (<see cref="AsnPackingListController"/>). Separate controller because two actions return a file, not the Result envelope.
/// </summary>
[ApiController]
[Authorize]
[Route("api/asns")]
public class AsnInvoiceReportController : ControllerBase
{
    private const string AvailabilityText = @"Availability (the Packing List's rule): a supplier with ASN Confirmation Required = Yes can print once the buyer has
**Approved** the ASN and after it is posted; a supplier with it = No can print from **Draft** and after posting. PendingApproval,
Rejected and Cancelled never have one.";

    private readonly IMediator _mediator;
    public AsnInvoiceReportController(IMediator mediator) => _mediator = mediator;

    [HttpGet("{id:guid}/invoice-report/groups")]
    [Authorize(Policy = Perm.AsnRead)]
    [Authorize(Policy = Perm.InvoiceRead)]
    [EndpointSummary("ASN Invoice reports (pre-Post) — list")]
    [EndpointDescription(@"2026-11-17 — the Supplier Invoice report(s) the ASN can print from its own lines, before (and after) Post: one per
(currency, payment term) group, grouped and ordered like the draft invoices Post creates. Nothing is written.
Filters / params:
- **id**: Required — ASN GUID.
Invoice number = the ASN's Invoice No. (suffixed '-n' when there are several groups), else 'DRAFT-{asnNumber}-{n}'.
" + AvailabilityText + @"
Returns: List<AsnInvoiceReportRefDto> (group, invoiceNumber, currency, paymentTerms, grandTotal). 404 outside the caller's scope;
409 when not available for the ASN's status; 400 when a line's tax code has no usable rate. Requires **Asn.Read** and **Invoice.Read**.")]
    public async Task<Result<IReadOnlyList<AsnInvoiceReportRefDto>>> Groups(Guid id, CancellationToken ct)
    {
        var reports = await _mediator.Send(new GetAsnInvoiceReportsQuery(id), ct);
        IReadOnlyList<AsnInvoiceReportRefDto> data = reports
            .Select((r, i) => new AsnInvoiceReportRefDto(i + 1, r.InvoiceNumber, r.Currency, r.PaymentTerms, r.GrandTotal))
            .ToList();
        return Result<IReadOnlyList<AsnInvoiceReportRefDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}/invoice-report")]
    [Authorize(Policy = Perm.AsnRead)]
    [Authorize(Policy = Perm.InvoiceRead)]
    [EndpointSummary("ASN Invoice report (pre-Post) — PDF preview")]
    [EndpointDescription(@"2026-11-17 — renders one of the ASN's Supplier Invoice reports (same layout as the invoice's R19 report) as an A4
PDF for **preview**, computed from the ASN lines: qty = the ASN's shipped qty per PO line; price, per-unit discount and tax rate from
the PO line / tax master. A pure read — nothing is written or attached.
Filters / params:
- **id**: Required — ASN GUID.
- **group**: Optional — 1-based report number from the groups list (default 1).
" + AvailabilityText + @"
Returns: application/pdf inline (filename 'Invoice-{invoiceNumber}.pdf'); 400 for an unknown group or a tax code without a rate;
404 outside the caller's scope; 409 when not available. Requires **Asn.Read** and **Invoice.Read**.")]
    public async Task<IActionResult> Get(
        Guid id, [FromServices] ISupplierInvoicePdfGenerator pdfGenerator, [FromQuery] int group = 1, CancellationToken ct = default)
    {
        var reports = await _mediator.Send(new GetAsnInvoiceReportsQuery(id), ct);
        if (group < 1 || group > reports.Count)
            return BadRequest(Result.Fail($"group must be between 1 and {reports.Count}."));

        var report = reports[group - 1];
        var bytes = pdfGenerator.Generate(report);
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
        {
            FileName = FileName(report.InvoiceNumber),
        }.ToString();
        return File(bytes, "application/pdf");
    }

    [HttpPost("{id:guid}/invoice-report/download")]
    [Authorize(Policy = Perm.AsnRead)]
    [Authorize(Policy = Perm.InvoiceRead)]
    [EndpointSummary("Download ASN Invoice report (pre-Post) + auto-attach for supplier users")]
    [EndpointDescription(@"2026-11-17 — the **download** twin of the ASN Invoice report preview (same PDF, same availability / 400 / 404 / 409),
returned as an attachment (filename 'Invoice-{invoiceNumber}.pdf').
Side effect (supplier users only): auto-attached to the **ASN** as a system-generated document of type 'EInvoice' when that slot is
enabled for the supplier (active type + entity + a policy). A download replaces the earlier generated file of the same group and any
stale generated invoice report (e.g. one printed before the Invoice No. was entered) while keeping the other groups' current reports;
manual uploads are never touched. Internal / buyer / admin downloads never
attach. Best-effort: a storage failure never fails the download. Header **X-Report-Attached** = 'true' | 'false'.
Filters / params:
- **id**: Required — ASN GUID.
- **group**: Optional — 1-based report number (default 1).
Requires **Asn.Read** and **Invoice.Read**.")]
    public async Task<IActionResult> Download(
        Guid id,
        [FromServices] ISupplierInvoicePdfGenerator pdfGenerator,
        [FromServices] GeneratedReportAttacher attacher,
        [FromServices] ILogger<AsnInvoiceReportController> logger,
        [FromQuery] int group = 1,
        CancellationToken ct = default)
    {
        var reports = await _mediator.Send(new GetAsnInvoiceReportsQuery(id), ct);
        if (group < 1 || group > reports.Count)
            return BadRequest(Result.Fail($"group must be between 1 and {reports.Count}."));

        var report = reports[group - 1];
        var bytes = pdfGenerator.Generate(report);
        var fileName = FileName(report.InvoiceNumber);

        var attached = false;
        try
        {
            attached = await attacher.AttachAsync(
                DocumentOwnerTypes.Asn, id, GeneratedReportTypes.EInvoice, fileName, bytes, ct,
                keepFileNames: reports.Select(r => FileName(r.InvoiceNumber)).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ASN invoice report for ASN {AsnId} (group {Group}) was generated but could not be auto-attached.", id, group);
        }

        Response.Headers["X-Report-Attached"] = attached ? "true" : "false";
        return File(bytes, "application/pdf", fileName);
    }

    /// <summary>'Invoice-{number}.pdf' with any character a file name cannot carry replaced (same rule as the invoice report).</summary>
    private static string FileName(string invoiceNumber)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(invoiceNumber.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return $"Invoice-{safe}.pdf";
    }
}
