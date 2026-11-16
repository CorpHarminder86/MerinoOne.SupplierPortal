using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Documents;
using MerinoOne.SupplierPortal.Application.Invoices.Commands;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Documents;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using ContractsPagedResult = MerinoOne.SupplierPortal.Contracts.PurchaseOrders.PagedResult<MerinoOne.SupplierPortal.Contracts.Invoices.InvoiceListItemDto>;
using MerinoOne.SupplierPortal.Contracts.Authorization;

namespace MerinoOne.SupplierPortal.Controllers;

[ApiController]
[Authorize]
[Route("api/invoices")]
public class InvoicesController : ControllerBase
{
    private readonly IMediator _mediator;
    public InvoicesController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    [Authorize(Policy = Perm.InvoiceRead)]
    [EndpointSummary("Invoice list")]
    [EndpointDescription(@"Paged list of supplier invoices visible to the caller.
Filters / params:
- **page**: Optional — 1-based page index (default 1).
- **pageSize**: Optional — rows per page (default 50).
- **status**: Optional — invoice lifecycle status (Submitted / Reviewed / Approved / Rejected / Paid).
- **supplierId**: Optional — restrict to one supplier.
- **purchaseOrderId**: Optional — restrict to one PO.
- **search**: Optional — free-text on invoice number / reference.
Side effects:
- Seccode-scoped: non-privileged users see only their suppliers' invoices.
Returns: PagedResult<InvoiceListItemDto>. Requires permission **Invoice.Read**.")]
    public async Task<Result<ContractsPagedResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null,
        [FromQuery] Guid? supplierId = null,
        [FromQuery] Guid? purchaseOrderId = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var data = await _mediator.Send(new GetInvoiceListQuery(page, pageSize, status, supplierId, purchaseOrderId, search), ct);
        return Result<ContractsPagedResult>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Perm.InvoiceRead)]
    [EndpointSummary("Invoice detail")]
    [EndpointDescription(@"Full invoice header + line items + linked PO / GR references + attachments.
Filters / params:
- **id**: Required — invoice GUID.
R6: header carries **invoiceOrigin** (SupplierManual | AsnGenerated); each line carries the frozen tax snapshot
(taxId / taxRatePct / taxDescription), its owning PO (purchaseOrderId / poNumber) and **remainingQty** — the LIVE
invoiceable balance of the PO line (shippedQtyToDate − invoicedQtyToDate) used as the Draft billedQty edit cap
(0 once the invoice is locked).
Returns: InvoiceDetailDto on success; 404 if not found; 403 if seccode mismatch. Requires permission **Invoice.Read**.")]
    public async Task<Result<InvoiceDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var data = await _mediator.Send(new GetInvoiceByIdQuery(id), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpGet("{id:guid}/pdf")]
    [Authorize(Policy = Perm.InvoiceRead)]
    [EndpointSummary("Supplier Invoice report (PDF preview)")]
    [EndpointDescription(@"R19 — renders the invoice's FROZEN values as the supplier-format Invoice report (supplier, bill-to, ship-to and
ship-from blocks, invoice / ASN / shipment header, one row per line with discount, taxable amount and tax, totals and the amount
in words) as an A4 PDF for **preview**. A pure read: nothing is written and nothing is attached. Seccode-scoped through the normal
invoice read (an invoice the caller cannot see is a 404, never a leak).
Filters / params:
- **id**: Required — invoice GUID.
Returns: application/pdf served **inline** (Content-Disposition: inline; filename 'Invoice-{invoiceNumber}.pdf'); 404 if the invoice
does not exist or is outside the caller's scope. To download it (and, for a supplier user, auto-attach it) use
POST api/invoices/{id}/pdf/download. Requires **Invoice.Read**.")]
    public async Task<IActionResult> GetPdf(
        Guid id, [FromServices] ISupplierInvoicePdfGenerator pdfGenerator, CancellationToken ct)
    {
        var report = await _mediator.Send(new GetSupplierInvoiceReportQuery(id), ct);
        var bytes = pdfGenerator.Generate(report);

        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
        {
            FileName = InvoiceReportFileName(report.InvoiceNumber),
        }.ToString();
        return File(bytes, "application/pdf");
    }

    [HttpPost("{id:guid}/pdf/download")]
    [Authorize(Policy = Perm.InvoiceRead)]
    [EndpointSummary("Download Supplier Invoice report (PDF) + auto-attach for supplier users")]
    [EndpointDescription(@"R19 — the **download** twin of the Invoice report preview: renders the same PDF and returns it as an attachment
(Content-Disposition: attachment; filename 'Invoice-{invoiceNumber}.pdf').
Side effect (supplier users only): the generated file is auto-attached as a **system-generated** document of type 'EInvoice' to the
owner chosen by **attachTo** — but only when that attachment slot is enabled for the invoice's supplier (active type + entity + a
policy). A repeat download REPLACES the earlier system-generated file (a manual upload of the same type is never touched); the
owner's lock state (ASN posted / invoice submitted) is ignored. An internal / buyer / admin download never attaches. The attach is
best-effort: a storage failure never fails the download. The response header **X-Report-Attached** is 'true' when the file was
attached and 'false' otherwise.
Filters / params:
- **id**: Required — invoice GUID.
- **attachTo**: Optional — 'invoice' (default; the file lands on this invoice) or 'asn' (it lands on the invoice's ASN); case-insensitive.
Returns: application/pdf attachment; 400 for an unknown attachTo, or attachTo=asn on an invoice that has no ASN ('Invoice has no ASN.');
404 if the invoice does not exist or is outside the caller's scope. Requires **Invoice.Read**.")]
    public async Task<IActionResult> DownloadPdf(
        Guid id,
        [FromServices] ISupplierInvoicePdfGenerator pdfGenerator,
        [FromServices] GeneratedReportAttacher attacher,
        [FromServices] IAppDbContext db,
        [FromServices] ILogger<InvoicesController> logger,
        [FromQuery] string? attachTo = null,
        CancellationToken ct = default)
    {
        var toAsn = false;
        if (!string.IsNullOrWhiteSpace(attachTo)
            && !string.Equals(attachTo, "invoice", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(attachTo, "asn", StringComparison.OrdinalIgnoreCase))
                return BadRequest(Result.Fail("attachTo must be 'invoice' or 'asn'."));
            toAsn = true;
        }

        // The owner is resolved BEFORE the (heavier) report is built, so a bad attachTo never costs a render. The invoice read is
        // RLS-scoped: an invoice the caller cannot see is a 404 here exactly as it is in the report query.
        var ownerType = DocumentOwnerTypes.Invoice;
        var ownerId = id;
        if (toAsn)
        {
            var row = await db.Invoices.AsNoTracking()
                .Where(i => i.Id == id && !i.IsDeleted)
                .Select(i => new { i.AsnId }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Invoice", id);
            if (row.AsnId is not { } asnId)
                return BadRequest(Result.Fail("Invoice has no ASN."));
            ownerType = DocumentOwnerTypes.Asn;
            ownerId = asnId;
        }

        var report = await _mediator.Send(new GetSupplierInvoiceReportQuery(id), ct);
        var bytes = pdfGenerator.Generate(report);
        var fileName = InvoiceReportFileName(report.InvoiceNumber);

        var attached = false;
        try
        {
            attached = await attacher.AttachAsync(ownerType, ownerId, GeneratedReportTypes.EInvoice, fileName, bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Invoice report for invoice {InvoiceId} was generated but could not be auto-attached to {OwnerType} {OwnerId}.",
                id, ownerType, ownerId);
        }

        Response.Headers["X-Report-Attached"] = attached ? "true" : "false";
        return File(bytes, "application/pdf", fileName);
    }

    [HttpGet("~/api/asns/{id:guid}/invoices")]
    [Authorize(Policy = Perm.InvoiceRead)]
    [EndpointSummary("Invoices of an ASN")]
    [EndpointDescription(@"R19 — the (non-deleted) invoices generated from / raised against one ASN, as light references (id, invoice number,
status, date), ordered by invoice date then invoice number. Used by the ASN screen to link to its invoice(s) and offer the Invoice report.
Filters / params:
- **id**: Required — ASN GUID.
Side effects:
- Seccode-scoped: only invoices the caller can see are returned, so an ASN of another supplier yields an empty list (never a leak).
Returns: List<AsnInvoiceRefDto> (empty when none). Requires permission **Invoice.Read**.")]
    public async Task<Result<IReadOnlyList<AsnInvoiceRefDto>>> ListForAsn(
        Guid id, [FromServices] IAppDbContext db, CancellationToken ct)
    {
        var rows = await db.Invoices.AsNoTracking()
            .Where(i => i.AsnId == id && !i.IsDeleted)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceNumber)
            .Select(i => new { i.Id, i.InvoiceNumber, i.InvoiceStatus, i.InvoiceDate })
            .ToListAsync(ct);

        IReadOnlyList<AsnInvoiceRefDto> data = rows
            .Select(r => new AsnInvoiceRefDto(r.Id, r.InvoiceNumber, r.InvoiceStatus.ToString(), r.InvoiceDate))
            .ToList();
        return Result<IReadOnlyList<AsnInvoiceRefDto>>.Ok(data, HttpContext.TraceIdentifier);
    }

    /// <summary>'Invoice-{number}.pdf' with any character a file name cannot carry (e.g. '/' in 'GST/24/001') replaced.</summary>
    private static string InvoiceReportFileName(string invoiceNumber)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(invoiceNumber.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return $"Invoice-{safe}.pdf";
    }

    [HttpPost]
    [Authorize(Policy = Perm.InvoiceSubmit)]
    [EndpointSummary("Create invoice (Draft)")]
    [EndpointDescription(@"Supplier creates a DRAFT invoice against a PO. REFACTORED: created as Draft (was
Submitted) — NO ERP post on create. The supplier edits it (PUT) and submits it (/submit) separately; posting is
GRN-gated (Module 5).
Body:
- **body**: CreateInvoiceRequest with PO reference, line items, tax breakdown.
Returns: InvoiceDetailDto (Draft) on success; 400 on validation; 403 if seccode mismatch. Requires **Invoice.Submit**.")]
    public async Task<Result<InvoiceDetailDto>> Create([FromBody] CreateInvoiceRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateInvoiceCommand(body), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("from-asn")]
    [Authorize(Policy = Perm.InvoiceSubmit)]
    [EndpointSummary("Create draft invoice(s) from an ASN (also the Blocked-generation Retry)")]
    [EndpointDescription(@"R6 — runs the grouped draft-invoice generation for a Submitted ASN (the same generator
that runs automatically at ASN approval): ONE Draft invoice per PO (currency, payment-term) group, numbered
DRAFT-{asnNumber}-{n}; per line billedQty = shippedQtyToDate − invoicedQtyToDate (live). Idempotent: existing
invoices for the ASN are returned, never re-created. Doubles as the **Retry generation** action for an ASN whose
InvoiceGenerationStatus is 'Blocked' — success clears the flag; a still-missing tax rate returns 400 with the
blocking reason (and re-persists the Blocked note).
Body:
- **body**: CreateInvoiceFromAsnRequest with the AsnId.
Returns: InvoiceDetailDto (the FIRST draft) on success; 404 if ASN not found; 409 if ASN not Submitted; 400 when
generation is blocked (tax rate gap) or nothing remains to invoice. Requires **Invoice.Submit**.")]
    public async Task<Result<InvoiceDetailDto>> CreateFromAsn([FromBody] CreateInvoiceFromAsnRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new CreateInvoiceFromAsnCommand(body), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Perm.InvoiceSubmit)]
    [EndpointSummary("Update invoice (Draft only)")]
    [EndpointDescription(@"Edits a DRAFT invoice (409 once Submitted). Header editable: invoiceNumber, invoiceDate,
eInvoiceIrn, eInvoiceAckNo, eWayBillNumber, notes.
R6 — optional **lines** array ({ invoiceLineId, billedQty, taxId }): billedQty must be ≥ 0 and ≤ the line's LIVE
remainingQty (shippedQtyToDate − invoicedQtyToDate; 400 over cap naming the line); taxId reselect re-resolves
code/description/rate server-side (a selected tax with no rate is a 400; taxId null clears the line's tax); line
and header amounts (invoiceAmount / taxAmount / netAmount = lines + tax) are recomputed server-side.
Returns: InvoiceDetailDto on success; 404 if not found; 409 if not Draft; 400 on duplicate number / cap breach. Requires **Invoice.Submit**.")]
    public async Task<Result<InvoiceDetailDto>> Update(Guid id, [FromBody] UpdateInvoiceRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new UpdateInvoiceCommand(id, body), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = Perm.InvoiceSubmit)]
    [EndpointSummary("Submit invoice (Draft -> Matched | MatchExceptions)")]
    [EndpointDescription(@"R6 — submits a DRAFT invoice. Guard order: real invoice number (the 'DRAFT-' / legacy
'INV-DRAFT-' placeholder prefixes are rejected) → invoiceDate → IRN / e-way bill when the Invoicing settings
(RequireIrn / RequireEWayBill) demand them → (supplier, invoiceNumber) uniqueness → attachment governance (§8.3:
mandatory-missing 400; warning-missing returns 200 **confirmationRequired=true**, nothing committed).
Then, atomically: each line's tax rate is RE-RESOLVED and FROZEN (a drift vs the draft rate is applied and
surfaced as an advisory in the response **notices** array, e.g. 'Tax GST18: rate changed 18% → 12%'); the
per-PO-line over-invoice reservation is taken (billed ≤ shippedQtyToDate − invoicedQtyToDate; a lost race returns
409 and nothing is reserved); local matching runs — TwoWay: reservation = matched; ThreeWay: additionally billed ≤
Σ receivedQty of the covering GRNs. Header lands **Matched** (all pass) or **MatchExceptions**; submittedAt/by are
stamped regardless. NOT posted to ERP here — posting stays GRN-gated (the auto-post claim accepts Submitted or
Matched; MatchExceptions never auto-posts).
Returns: InvoiceDetailDto (Matched/MatchExceptions) + notices[] on success; 200 confirmationRequired on a Warning
skip; 404 if not found; 409 if not Draft / reservation lost; 400 on validation. Requires **Invoice.Submit**.")]
    public async Task<Result<InvoiceDetailDto>> Submit(Guid id, [FromBody] SubmitInvoiceRequest? body, CancellationToken ct)
    {
        var outcome = await _mediator.Send(new SubmitInvoiceCommand(id, body ?? new SubmitInvoiceRequest()), ct);
        return outcome.ToResult(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/revoke")]
    [Authorize(Policy = Perm.InvoiceRevoke)]
    [EndpointSummary("Revoke invoice (admin, pre-post)")]
    [EndpointDescription(@"Admin PRE-POST revoke: Submitted/Matched/MatchExceptions -> Draft (NO LN reversal —
nothing was posted). Guards: state (a submit-side status AND not yet posted) and optimistic concurrency via
RowVersion (a stale token yields 409 against a racing auto-post). R6 — releases the invoice's per-PO-line
invoiced-qty reservation (invoicedQtyToDate is decremented, floored at 0) in the same transaction, so the
quantities become re-invoiceable.
Body:
- **body**: RevokeInvoiceRequest with an optional reason + the RowVersion (base64, from the detail DTO).
Returns: InvoiceDetailDto (Draft) on success; 404 if not found; 409 if not revocable/already posted/stale
RowVersion. Requires **Invoice.Revoke** (admin/Finance).")]
    public async Task<Result<InvoiceDetailDto>> Revoke(Guid id, [FromBody] RevokeInvoiceRequest body, CancellationToken ct)
    {
        var data = await _mediator.Send(new RevokeInvoiceCommand(id, body), ct);
        return Result<InvoiceDetailDto>.Ok(data, HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/review")]
    [Authorize(Policy = Perm.InvoiceReview)]
    [EndpointSummary("Review invoice")]
    [EndpointDescription(@"Buyer reviewer flags an invoice as triaged before sending it for approval.
Filters / params:
- **id**: Required — invoice GUID.
- **body**: Optional — ReviewInvoiceRequest with reviewer notes.
Side effects:
- Flips status to Reviewed + stamps reviewer/timestamp.
Returns: empty success; 404 if not found; 409 if not in reviewable state. Requires permission **Invoice.Review**.")]
    public async Task<Result> Review(Guid id, [FromBody] ReviewInvoiceRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ReviewInvoiceCommand(id, body ?? new ReviewInvoiceRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Perm.InvoiceApprove)]
    [EndpointSummary("Approve invoice")]
    [EndpointDescription(@"Finance approves a reviewed invoice for payment.
Filters / params:
- **id**: Required — invoice GUID.
- **body**: Optional — ApproveInvoiceRequest with approver notes.
Side effects:
- Flips status to Approved + stamps approver/timestamp.
- Becomes eligible for the next payment run.
Returns: empty success; 404 if not found; 409 if not in approvable state. Requires permission **Invoice.Approve**.")]
    public async Task<Result> Approve(Guid id, [FromBody] ApproveInvoiceRequest? body, CancellationToken ct)
    {
        await _mediator.Send(new ApproveInvoiceCommand(id, body ?? new ApproveInvoiceRequest()), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Perm.InvoiceApprove)]
    [EndpointSummary("Reject invoice")]
    [EndpointDescription(@"Finance rejects an invoice; supplier must amend + resubmit.
Filters / params:
- **id**: Required — invoice GUID.
- **body**: RejectInvoiceRequest with required reason.
Side effects:
- Flips status to Rejected + records reason + timestamp.
- Notifies the supplier via the configured email template.
Returns: empty success; 404 if not found; 409 if not in rejectable state. Requires permission **Invoice.Approve**.")]
    public async Task<Result> Reject(Guid id, [FromBody] RejectInvoiceRequest body, CancellationToken ct)
    {
        await _mediator.Send(new RejectInvoiceCommand(id, body), ct);
        return Result.Ok(HttpContext.TraceIdentifier);
    }
}
