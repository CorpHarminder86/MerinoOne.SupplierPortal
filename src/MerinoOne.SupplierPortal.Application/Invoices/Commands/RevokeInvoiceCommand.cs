using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Invoices.Queries;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 4 (Q9). Admin <b>pre-post</b> revoke of an invoice: <c>Submitted -> Draft</c>. This is
/// a pure status flip — NO LN reversal call (auto-post only picks Submitted, so reverting to Draft removes it from
/// the posting set; nothing was posted yet). Because a concurrent GRN-gated auto-post could be racing this revoke,
/// the operation is guarded:
/// <list type="bullet">
///   <item>policy <c>Invoice.Revoke</c> (admin/Finance — already seeded) on the controller;</item>
///   <item>state guard: only <c>Submitted</c> AND <c>erpPostedAt IS NULL</c> (pre-post) — else 409;</item>
///   <item>optimistic concurrency: the client's RowVersion is applied as the EF concurrency token; a stale token
///         (someone else changed the row) yields 409, never a silent overwrite.</item>
/// </list>
///
/// <para><b>Review R3 — clear the auto-post latch so a re-submit can re-post.</b> Revoke is allowed when the post
/// was merely <i>initiated</i> (<c>erpPostInitiatedAt</c> set, <c>erpPostedAt</c> still null — e.g. a dispatch
/// failed/in-flight). <c>erpPostInitiatedAt</c> is a one-way latch that the GRN auto-post claim
/// (<c>WHERE erpPostInitiatedAt IS NULL</c>) tests — so leaving it set would make a revoke→re-submit→re-approve
/// invoice the claim can NEVER win again (silently never auto-posts). This handler therefore CLEARS
/// <c>erpPostInitiatedAt</c> and the stale <c>erpSyncId</c> (the post-key correlation handle) on revoke, AND
/// soft-deletes any non-<c>Acked</c> <c>InvoicePost</c> outbox row for this invoice — otherwise the deterministic-key
/// idempotency probe / composite UQ would block the re-enqueue on re-approval. After a revoke, the clean Draft can
/// be re-submitted and re-approved, and the GRN cascade re-claims and re-posts it.</para>
/// </summary>
public record RevokeInvoiceCommand(Guid Id, RevokeInvoiceRequest Body) : IRequest<InvoiceDetailDto>;

public class RevokeInvoiceCommandValidator : AbstractValidator<RevokeInvoiceCommand>
{
    public RevokeInvoiceCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Body.Reason).MaximumLength(1000);
    }
}

public class RevokeInvoiceCommandHandler : IRequestHandler<RevokeInvoiceCommand, InvoiceDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMediator _mediator;

    public RevokeInvoiceCommandHandler(IAppDbContext db, ICurrentUser user, IMediator mediator)
    {
        _db = db; _user = user; _mediator = mediator;
    }

    public async Task<InvoiceDetailDto> Handle(RevokeInvoiceCommand request, CancellationToken ct)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == request.Id, ct)
                      ?? throw new NotFoundException("Invoice", request.Id);

        // Pre-post only (Q9): must be Submitted and not yet posted to ERP.
        if (invoice.InvoiceStatus != InvoiceStatus.Submitted || invoice.ErpPostedAt.HasValue)
            throw new ConflictException(
                $"Only a Submitted, not-yet-posted invoice can be revoked; current state '{invoice.InvoiceStatus}'" +
                (invoice.ErpPostedAt.HasValue ? " (already posted to ERP)." : "."));

        // Apply the client's RowVersion as the concurrency token so a stale revoke (vs. a racing auto-post) fails 409.
        if (!string.IsNullOrWhiteSpace(request.Body.RowVersion))
        {
            try { invoice.RowVersion = Convert.FromBase64String(request.Body.RowVersion); }
            catch (FormatException) { throw new ConflictException("Invalid RowVersion; reload the invoice and retry."); }
        }

        var now = DateTime.UtcNow;
        invoice.InvoiceStatus = InvoiceStatus.Draft;
        invoice.RevokedBy = _user.UserCode;
        invoice.RevokedAt = now;
        invoice.RevokeReason = string.IsNullOrWhiteSpace(request.Body.Reason) ? null : request.Body.Reason!.Trim();
        // Clear the prior submit stamp so the Draft is clean for re-submit.
        invoice.SubmittedAt = null;
        invoice.SubmittedBy = null;

        // Review R3 — CLEAR the auto-post latch. erpPostInitiatedAt is a one-way latch the GRN cascade's claim
        // (WHERE erpPostInitiatedAt IS NULL) tests; leaving it set would make a re-submitted, re-approved invoice
        // un-claimable forever (never auto-posts). Pre-post revoke means erpPostedAt is null (guarded above), so the
        // post never landed — it is safe to clear the initiated marker and the stale erpSyncId post-key handle.
        invoice.ErpPostInitiatedAt = null;
        invoice.ErpSyncId = null;
        invoice.UpdatedBy = _user.UserCode;
        invoice.UpdatedOn = now;

        // Review R3 — soft-delete any non-Acked InvoicePost outbox row for THIS invoice. The enqueue idempotency
        // probe (OutboxDispatcher) and the composite UQ_OutboxMessage_tenant_deterministicKey are both filtered on
        // [isDeleted] = 0, so a live Pending/Sending/Dispatched/Failed row would block the re-enqueue on the next
        // GRN re-approval. An already-Acked row is left intact (it represents a genuine prior post that this
        // pre-post revoke is NOT undoing; in practice erpPostedAt would be set in that case and revoke is blocked
        // above). Mutated as tracked rows so they soft-delete in the SAME SaveChanges as the invoice revoke.
        var outboxRows = await _db.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => !m.IsDeleted
                        && m.EntityId == invoice.Id
                        && m.TransactionType == OutboxTransactionType.InvoicePost
                        && m.Status != OutboxStatus.Acked)
            .ToListAsync(ct);
        foreach (var row in outboxRows)
        {
            row.IsDeleted = true;
            row.LastError = $"Soft-deleted: invoice revoked (pre-post) at {now:O} by {_user.UserCode}.";
            row.UpdatedBy = _user.UserCode;
            row.UpdatedOn = now;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent change (e.g. the GRN-gated auto-post) won the race — surface as 409 (no silent overwrite).
            throw new ConflictException("The invoice was modified concurrently (possibly posted). Reload and retry.");
        }

        return await _mediator.Send(new GetInvoiceByIdQuery(invoice.Id), ct);
    }
}
