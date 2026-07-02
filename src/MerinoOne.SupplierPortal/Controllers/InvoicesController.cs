using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.Invoices.Commands;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    [EndpointSummary("Invoice PDF (frozen snapshot)")]
    [EndpointDescription(@"Renders the invoice's FROZEN snapshot (header, supplier, lines with tax rate/amounts,
per-line PO, totals, IRN/ack/e-way) as a PDF. Seccode-scoped through the normal detail query (404 via filters,
never a leak). Returns: application/pdf file named '{invoiceNumber}.pdf'. Requires **Invoice.Read**.")]
    public async Task<IActionResult> GetPdf(
        Guid id, [FromServices] IInvoicePdfGenerator pdfGenerator, CancellationToken ct)
    {
        var invoice = await _mediator.Send(new GetInvoiceByIdQuery(id), ct);
        var bytes = pdfGenerator.Generate(invoice);
        return File(bytes, "application/pdf", $"{invoice.InvoiceNumber}.pdf");
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
