using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ForbiddenException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ForbiddenException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

// ============================================================================================================
// R5 (TSD R5 Addendum §10) — ASN approval lifecycle: SendForApproval (supplier) → Approve / Reject (buyer).
//
// R14 (2026-08-05) RE-TIMES both checks AGAIN, and breaks approval apart from the ERP dispatch:
//   • Attachment-requirement check moves from SEND-FOR-APPROVAL to POST (PostAsnCommand) — the whole point of
//     the confirmation flow is that the supplier may send for approval BEFORE the documents exist.
//   • The three mandatory shipment references move from SEND-FOR-APPROVAL to POST, for the same reason.
//   • Approve no longer runs AsnSubmitExecutor. It sets AsnStatus.Approved and stops. The over-ship atomic
//     balance consumption, draft-invoice generation and LN outbox enqueue all now fire at POST.
// ============================================================================================================

// ─────────────────────────────────── Send for Approval (supplier) ──────────────────────────────────────────

/// <summary>
/// R5 §10.2 — Draft → PendingApproval. Creates the <see cref="AsnApproval"/> session (Pending), resolves the
/// buyer approver(s), and notifies them (best-effort).
///
/// <para>R14 — this step is now DELIBERATELY PERMISSIVE. The attachment-requirement check and the three
/// mandatory shipment references both moved to <c>PostAsnCommand</c>, because the requirement is explicitly
/// that a supplier may send an ASN for buyer confirmation before the Packing List / Invoice exist. Only the
/// PO ship gate still applies here. Rejected for a supplier with
/// <c>AsnConfirmationRequired = false</c> — that supplier has no approval step and must post directly.</para>
///
/// <para>The return type stays <see cref="SubmitOutcome{T}"/> for wire compatibility with the existing
/// two-step client, but with the attachment guard gone this path now only ever returns Completed.</para>
/// </summary>
public record SendForApprovalCommand(Guid Id, bool AcknowledgeMissingAttachments = false)
    : IRequest<SubmitOutcome<AsnDetailDto>>;

public class SendForApprovalCommandValidator : AbstractValidator<SendForApprovalCommand>
{
    public SendForApprovalCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class SendForApprovalCommandHandler : IRequestHandler<SendForApprovalCommand, SubmitOutcome<AsnDetailDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IFulfilmentSettings _fulfilment;

    public SendForApprovalCommandHandler(IAppDbContext db, ICurrentUser user, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _fulfilment = fulfilment;
    }

    public async Task<SubmitOutcome<AsnDetailDto>> Handle(SendForApprovalCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        // Authorization (§10): only the owning supplier may send for approval. The seccode RLS already scopes the
        // ASN to the supplier's principal; canWrite is enforced by the Asn.Write policy + the supplier SecRight.
        AsnLifecycle.AssertCanSendForApproval(asn.AsnStatus);

        // R14 — this supplier may not have an approval step at all. Fail loudly rather than parking the ASN in a
        // PendingApproval state no buyer is watching (no buyer notification queue exists for a No-mode supplier).
        var confirmationRequired = await _db.Suppliers
            .Where(s => s.Id == asn.SupplierId)
            .Select(s => s.AsnConfirmationRequired)
            .FirstOrDefaultAsync(ct);
        if (!confirmationRequired)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["asnConfirmation"] = new[]
                {
                    "This supplier does not require buyer confirmation for ASNs; post the ASN directly instead of sending it for approval."
                }
            });

        // R14 — the shipment-reference check and the attachment-requirement check that used to live here BOTH
        // moved to PostAsnCommand. Sending for approval with no documents and no references is the requirement.

        var now = DateTime.UtcNow;

        // R4 §6.2 — "submission of a Draft ASN" is inside the gate block scope. Hard-block Send-For-Approval if a
        // covered PO is not shippable (e.g. reset to Released by an ERP Modify) or another ASN for the same PO is
        // already in flight. (The over-ship balance guard runs later, at Post.)
        // R14 fix (F1) — resolve covered POs from all three sources, not the junction alone: a schedule-built ASN
        // carries its PO linkage on AsnLine → PurchaseOrderLine and used to slip this gate entirely.
        var coveredPoIds = await AsnApprovalSupport.ResolveCoveredPoIdsAsync(_db, asn, ct);
        await AsnDraftGate.EnsureEditableAsync(_db, asn, coveredPoIds.ToList(), ct);

        // ---- Create the approval session (Pending) + flip the ASN to PendingApproval ---------------------
        var approval = new AsnApproval
        {
            Id = Guid.NewGuid(),
            AsnId = asn.Id,
            Status = AsnApprovalStatus.Pending,
            SubmittedBy = _user.UserCode,
            SubmittedOn = now,
            SeccodeId = asn.SeccodeId,
            TenantId = asn.TenantId,
            TenantEntityId = asn.TenantEntityId,
            CreatedBy = _user.UserCode,
            CreatedOn = now,
        };
        _db.AsnApprovals.Add(approval);

        asn.AsnStatus = AsnStatus.PendingApproval;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = now;

        // ---- Resolve approver buyers + notify (best-effort, EmailOutbox pattern) -------------------------
        var buyers = await AsnApprovalSupport.ResolveApproverUserIdsAsync(_db, asn, ct);
        await AsnApprovalSupport.NotifyBuyersForApprovalAsync(_db, asn, buyers.ToList(), now, ct);

        await _db.SaveChangesAsync(ct);

        var dto = await AsnDtoBuilder.BuildAsync(_db, asn.Id, ct, _fulfilment.OverShipAllowanceRounding);
        return SubmitOutcome<AsnDetailDto>.Completed(dto);
    }
}

// ─────────────────────────────────── Approve (buyer) → Submit ───────────────────────────────────────────────

/// <summary>
/// R5 §10.2, as re-cut by R14 — PendingApproval → <b>Approved</b>. Any ONE mapped PO buyer may approve. Sets the
/// approval session Approved (DecisionBy/On) and the ASN to Approved, and notifies the supplier that it may now
/// upload its documents and post.
///
/// <para><b>This no longer submits anything to the ERP.</b> Before R14 this handler called
/// <see cref="AsnSubmitExecutor"/> inline, so approval consumed PO balance, generated the draft invoice and
/// enqueued the LN post in one transaction. All of that moved to <c>PostAsnCommand</c>, because the buyer's
/// confirmation now happens BEFORE the supplier has finished the shipment (documents, references), and the
/// shipping date must be the post date rather than the approval date.</para>
///
/// <para><paramref name="OverrideReason"/> is retained on the wire for compatibility but is IGNORED here — the
/// PO confirmation-gate override belongs to whoever triggers the submit, which is now the Post caller.</para>
/// </summary>
public record ApproveAsnCommand(Guid Id, string? OverrideReason = null) : IRequest<AsnDetailDto>;

public class ApproveAsnCommandValidator : AbstractValidator<ApproveAsnCommand>
{
    public ApproveAsnCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class ApproveAsnCommandHandler : IRequestHandler<ApproveAsnCommand, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IFulfilmentSettings _fulfilment;

    public ApproveAsnCommandHandler(IAppDbContext db, ICurrentUser user, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _fulfilment = fulfilment;
    }

    public async Task<AsnDetailDto> Handle(ApproveAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        AsnLifecycle.AssertCanApprove(asn.AsnStatus);
        await AsnApprovalGate.AssertBuyerAsync(_db, _user, asn, ct);

        var now = DateTime.UtcNow;

        var approval = await _db.AsnApprovals
            .Where(a => a.AsnId == asn.Id && a.Status == AsnApprovalStatus.Pending)
            .OrderByDescending(a => a.SubmittedOn)
            .FirstOrDefaultAsync(ct)
            ?? throw new ValidationException(new Dictionary<string, string[]>
            {
                ["approval"] = new[] { "No pending approval session exists for this ASN." }
            });

        approval.Status = AsnApprovalStatus.Approved;
        approval.DecisionBy = _user.UserCode;
        approval.DecisionOn = now;
        approval.UpdatedBy = _user.UserCode;
        approval.UpdatedOn = now;

        // R14 — the buyer's confirmation, and nothing more. No balance is consumed, no invoice is drafted and
        // nothing is enqueued to LN here; the supplier's Post does all of that. The ASN becomes editable again
        // (AsnLifecycle.AssertCanEdit / AsnDtoBuilder.IsLocked both admit Approved) so the supplier can attach
        // the Packing List and Invoice and fill in the shipment references.
        asn.AsnStatus = AsnStatus.Approved;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = now;

        // R5 §20 — notify the supplier (the user who sent it for approval). R14 rewords it: approval is no longer
        // the end of the road, it is the supplier's cue to finish and post.
        await AsnApprovalSupport.NotifySupplierApprovedAsync(_db, asn, approval.SubmittedBy, now, ct);

        await _db.SaveChangesAsync(ct);

        return await AsnDtoBuilder.BuildAsync(_db, asn.Id, ct, _fulfilment.OverShipAllowanceRounding);
    }
}

// ─────────────────────────────────── Reject (buyer) → Rejected ──────────────────────────────────────────────

/// <summary>
/// R5 §10.2 — PendingApproval → Rejected. Reason is MANDATORY. No balance is consumed (none was consumed at
/// create/send — §10.4), so a rejected ASN needs NO reversal. The supplier may edit it (→ Draft) and re-raise.
/// </summary>
public record RejectAsnCommand(Guid Id, string Reason) : IRequest<AsnDetailDto>;

public class RejectAsnCommandValidator : AbstractValidator<RejectAsnCommand>
{
    public RejectAsnCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A rejection reason is required.")
            .MaximumLength(2000);
    }
}

public class RejectAsnCommandHandler : IRequestHandler<RejectAsnCommand, AsnDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IFulfilmentSettings _fulfilment;

    public RejectAsnCommandHandler(IAppDbContext db, ICurrentUser user, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _fulfilment = fulfilment;
    }

    public async Task<AsnDetailDto> Handle(RejectAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        AsnLifecycle.AssertCanReject(asn.AsnStatus);
        await AsnApprovalGate.AssertBuyerAsync(_db, _user, asn, ct);

        var now = DateTime.UtcNow;

        var approval = await _db.AsnApprovals
            .Where(a => a.AsnId == asn.Id && a.Status == AsnApprovalStatus.Pending)
            .OrderByDescending(a => a.SubmittedOn)
            .FirstOrDefaultAsync(ct)
            ?? throw new ValidationException(new Dictionary<string, string[]>
            {
                ["approval"] = new[] { "No pending approval session exists for this ASN." }
            });

        approval.Status = AsnApprovalStatus.Rejected;
        approval.DecisionBy = _user.UserCode;
        approval.DecisionOn = now;
        approval.Reason = request.Reason.Trim();
        approval.UpdatedBy = _user.UserCode;
        approval.UpdatedOn = now;

        asn.AsnStatus = AsnStatus.Rejected;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = now;

        // Notify the supplier user who submitted, with the reason (best-effort).
        await AsnApprovalSupport.NotifySupplierRejectedAsync(_db, asn, approval.SubmittedBy, approval.Reason!, now, ct);

        await _db.SaveChangesAsync(ct);

        return await AsnDtoBuilder.BuildAsync(_db, asn.Id, ct, _fulfilment.OverShipAllowanceRounding);
    }
}

/// <summary>
/// R5 §10.2 — the authorization gate for Approve/Reject: the current user must be an INTERNAL user MAPPED to the
/// ASN's supplier (<c>admin.SupplierUserMap</c>) — any one such approver may decide. Admins bypass (they oversee
/// the whole queue). Throws <see cref="ForbiddenException"/> (→ 403) otherwise.
/// </summary>
internal static class AsnApprovalGate
{
    public static async Task AssertBuyerAsync(IAppDbContext db, ICurrentUser user, Asn asn, CancellationToken ct)
    {
        // Admins oversee the whole queue → may approve/reject any ASN.
        if (user.IsAdmin) return;

        var approvers = await AsnApprovalSupport.ResolveApproverUserIdsAsync(db, asn, ct);
        if (approvers.Count == 0)
            throw new ForbiddenException("This ASN's supplier has no mapped internal users; it cannot be approved or rejected.");

        var myId = await db.AppUsers.IgnoreQueryFilters()
            .Where(u => u.UserCode == user.UserCode && !u.IsDeleted)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

        if (myId is not { } id || !approvers.Contains(id))
            throw new ForbiddenException("Only an internal user mapped to this ASN's supplier may approve or reject it.");
    }
}
