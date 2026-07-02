using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 4. Edits a <b>Draft</b> invoice only (409 once Submitted). Header editable set:
/// invoiceNumber, invoiceDate, eInvoiceIrn, eInvoiceAckNo, eWayBillNumber, notes. Enforces the
/// (supplier, invoiceNumber) uniqueness rule.
///
/// <para>R6 (2026-07-02) — optional <c>Lines</c>: per line, <c>BilledQty</c> (≥ 0 and ≤ the LIVE invoiceable
/// balance <c>shippedQtyToDate − invoicedQtyToDate</c>; 400 over cap, naming the line) and a tax reselect
/// (<c>TaxId</c> re-resolved via <see cref="TaxRateResolver"/> — code/description/rate are NEVER client-typed;
/// an explicitly selected tax with no rate is a 400). LineAmount/TaxAmount and the header
/// InvoiceAmount/TaxAmount/NetAmount (= lines + tax) are recomputed server-side.</para>
/// </summary>
public record UpdateInvoiceCommand(Guid Id, UpdateInvoiceRequest Body) : IRequest<InvoiceDetailDto>;

public class UpdateInvoiceCommandValidator : AbstractValidator<UpdateInvoiceCommand>
{
    public UpdateInvoiceCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.InvoiceNumber).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.InvoiceDate).NotEmpty();
        RuleFor(x => x.Body.EInvoiceIrn).MaximumLength(100);
        RuleFor(x => x.Body.EInvoiceAckNo).MaximumLength(100);
        RuleFor(x => x.Body.EWayBillNumber).MaximumLength(100);
        RuleForEach(x => x.Body.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.InvoiceLineId).NotEmpty();
            line.RuleFor(l => l.BilledQty).GreaterThanOrEqualTo(0);
        }).When(x => x.Body.Lines is not null);
    }
}

public class UpdateInvoiceCommandHandler : IRequestHandler<UpdateInvoiceCommand, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMediator _mediator;
    private readonly TaxRateResolver _taxResolver;

    public UpdateInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, IMediator mediator, TaxRateResolver taxResolver)
    {
        _db = db; _user = user; _mediator = mediator; _taxResolver = taxResolver;
    }

    public async Task<InvoiceDetailDto> Handle(UpdateInvoiceCommand request, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.Id, ct)
                      ?? throw new NotFoundException("Invoice", request.Id);

        if (invoice.InvoiceStatus != InvoiceStatus.Draft)
            throw new ConflictException($"Invoice is '{invoice.InvoiceStatus}'; only a Draft invoice can be edited.");

        var body = request.Body;
        var trimmedNumber = body.InvoiceNumber.Trim();
        var now = DateTime.UtcNow;

        // (supplier, invoiceNumber) uniqueness — excluding this invoice.
        if (!string.Equals(trimmedNumber, invoice.InvoiceNumber, StringComparison.Ordinal))
        {
            var dup = await _db.Invoices.AnyAsync(
                i => i.SupplierId == invoice.SupplierId && i.InvoiceNumber == trimmedNumber && i.Id != invoice.Id, ct);
            if (dup)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["invoiceNumber"] = new[] { $"Invoice number '{trimmedNumber}' already exists for this supplier." }
                });
        }

        invoice.InvoiceNumber = trimmedNumber;
        invoice.InvoiceDate = body.InvoiceDate;
        invoice.EInvoiceIrn = string.IsNullOrWhiteSpace(body.EInvoiceIrn) ? null : body.EInvoiceIrn.Trim();
        invoice.EInvoiceAckNo = string.IsNullOrWhiteSpace(body.EInvoiceAckNo) ? null : body.EInvoiceAckNo.Trim();
        invoice.EWayBillNumber = string.IsNullOrWhiteSpace(body.EWayBillNumber) ? null : body.EWayBillNumber.Trim();
        invoice.Notes = body.Notes;

        // ---- R6 — optional Draft line edits (billedQty + tax reselect) ----------------------------------
        if (body.Lines is { Count: > 0 })
        {
            var lines = await _db.InvoiceLines.Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct);
            var byId = lines.ToDictionary(l => l.Id);

            var poLineIds = lines.Select(l => l.PurchaseOrderLineId).Distinct().ToList();
            var balances = await _db.PurchaseOrderLines
                .Where(p => poLineIds.Contains(p.Id))
                .Select(p => new { p.Id, p.ShippedQtyToDate, p.InvoicedQtyToDate })
                .ToDictionaryAsync(p => p.Id, ct);

            var resolved = await _taxResolver.ResolveAsync(
                body.Lines.Where(r => r.TaxId.HasValue).Select(r => r.TaxId!.Value), invoice.TenantId, ct);

            var errors = new Dictionary<string, string[]>();
            foreach (var req in body.Lines)
            {
                if (!byId.TryGetValue(req.InvoiceLineId, out var line))
                {
                    errors[$"lines[{req.InvoiceLineId}]"] = new[] { "Invoice line not found on this invoice." };
                    continue;
                }

                // Server-side cap: the LIVE invoiceable balance of the PO line (a Draft holds no reservation).
                var bal = balances.TryGetValue(line.PurchaseOrderLineId, out var b) ? b : null;
                var remaining = bal is null ? 0m : Math.Max(0m, bal.ShippedQtyToDate - bal.InvoicedQtyToDate);
                if (req.BilledQty < 0 || req.BilledQty > remaining)
                {
                    errors[$"lines[{req.InvoiceLineId}]"] = new[]
                    {
                        $"Billed qty {req.BilledQty} for item '{line.ItemCode}' exceeds the remaining invoiceable " +
                        $"balance {remaining} (shipped − already invoiced)."
                    };
                    continue;
                }

                line.BilledQty = req.BilledQty;

                if (req.TaxId.HasValue)
                {
                    // Reselect ⇒ re-resolve — the client NEVER types a rate. An explicitly selected tax with no
                    // usable rate is rejected (the Draft must stay submittable).
                    if (!resolved.TryGetValue(req.TaxId.Value, out var tax))
                    {
                        errors[$"lines[{req.InvoiceLineId}]"] = new[] { "The selected tax code was not found." };
                        continue;
                    }
                    if (tax.Rate is null)
                    {
                        errors[$"lines[{req.InvoiceLineId}]"] = new[]
                        {
                            $"Tax '{tax.Code}' has no rate; select a tax code with a configured rate."
                        };
                        continue;
                    }
                    line.TaxId = req.TaxId;
                    line.TaxRatePct = tax.Rate;
                    line.TaxCode = tax.Code;
                    line.TaxDescription = tax.Description;
                }
                else
                {
                    // TaxId null = clear the line's tax.
                    line.TaxId = null;
                    line.TaxRatePct = null;
                    line.TaxCode = null;
                    line.TaxDescription = null;
                }

                line.LineAmount = decimal.Round(line.BilledQty * line.UnitPrice, 2);
                line.TaxAmount = line.TaxRatePct is { } rate
                    ? decimal.Round(line.LineAmount * rate / 100m, 2)
                    : 0m;
                line.UpdatedBy = _user.UserCode;
                line.UpdatedOn = now;
            }

            if (errors.Count > 0)
                throw new ValidationException(errors);

            // Header totals — line-level rounding first, then sum the rounded lines (money-math convention).
            invoice.InvoiceAmount = lines.Sum(l => l.LineAmount);
            invoice.TaxAmount = lines.Sum(l => l.TaxAmount);
            invoice.NetAmount = invoice.InvoiceAmount + invoice.TaxAmount;
        }

        invoice.UpdatedBy = _user.UserCode;
        invoice.UpdatedOn = now;

        await _db.SaveChangesAsync(ct);

        return await _mediator.Send(new GetInvoiceByIdQuery(invoice.Id), ct);
    }
}
