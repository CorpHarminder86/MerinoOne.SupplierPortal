using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Documents;
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
// The two R4 checks are RE-TIMED here:
//   • Attachment-requirement check fires at SEND-FOR-APPROVAL (§10.3), NOT at submit.
//   • Over-ship atomic balance consumption fires at the APPROVE→SUBMIT path (§10.4) via AsnSubmitExecutor.
// ============================================================================================================

// ─────────────────────────────────── Send for Approval (supplier) ──────────────────────────────────────────

/// <summary>
/// R5 §10.2 / §10.3 — Draft → PendingApproval. Runs the attachment-requirement check (MOVED here from Submit),
/// creates the <see cref="AsnApproval"/> session (Pending), resolves the buyer approver(s), and notifies them
/// (best-effort). Returns the two-step confirm outcome when a Warning attachment is unacknowledged.
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
    private readonly AttachmentSubmitGuard _attachmentGuard;
    private readonly IFulfilmentSettings _fulfilment;

    public SendForApprovalCommandHandler(
        IAppDbContext db, ICurrentUser user, AttachmentSubmitGuard attachmentGuard, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _attachmentGuard = attachmentGuard; _fulfilment = fulfilment;
    }

    public async Task<SubmitOutcome<AsnDetailDto>> Handle(SendForApprovalCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        // Authorization (§10): only the owning supplier may send for approval. The seccode RLS already scopes the
        // ASN to the supplier's principal; canWrite is enforced by the Asn.Write policy + the supplier SecRight.
        AsnLifecycle.AssertCanSendForApproval(asn.AsnStatus);

        var now = DateTime.UtcNow;

        // ---- Attachment Requirement Governance (MOVED from Submit, §10.3) --------------------------------
        // Mandatory missing → throws (400). Warning missing + not-acknowledged → ConfirmationRequired (no mutation).
        // Acknowledged-skip stages a skip AuditEntry committing in this handler's SaveChanges.
        var decision = await _attachmentGuard.EvaluateAsync(
            _db, DocumentOwnerTypes.Asn, asn.Id, asn.AsnNumber, asn.SupplierId,
            request.AcknowledgeMissingAttachments, asn.TenantId, now, ct);
        if (decision.RequiresConfirmation)
            return SubmitOutcome<AsnDetailDto>.Confirm(decision.MissingWarning);

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
/// R5 §10.2 / §10.4 — PendingApproval → Submitted. Any ONE mapped PO buyer may approve (Phase 1). Sets the
/// approval session Approved (DecisionBy/On), then runs the SUBMIT path through <see cref="AsnSubmitExecutor"/> —
/// the over-ship atomic guard consumes balance, the ASN flips to Submitted, the draft invoice + ERP outbox are
/// created. If the guard returns 0 rows (balance lost post-approval, UC-AP-05) the submit fails (400) and the ASN
/// stays PendingApproval — the approval did not flip (the whole handler rolls back).
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
    private readonly AsnSubmitExecutor _submit;
    private readonly IFulfilmentSettings _fulfilment;

    public ApproveAsnCommandHandler(
        IAppDbContext db, ICurrentUser user, AsnSubmitExecutor submit, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _submit = submit; _fulfilment = fulfilment;
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

        // R5 §20 — notify the supplier (the user who sent it for approval) that the buyer approved it. Staged on the
        // SAME context BEFORE the executor's SaveChanges, so the email row commits in the executor's transaction and
        // rolls back with it on a UC-AP-05 submit failure (no false "approved" email if the shipment can't proceed).
        await AsnApprovalSupport.NotifySupplierApprovedAsync(_db, asn, approval.SubmittedBy, now, ct);

        // The submit path: consumes balance (over-ship guard, §10.4), flips Submitted, draft invoice + outbox.
        // The approval mutation above is tracked on the same context and commits inside the executor's SaveChanges
        // + transaction — so a guard 0-row failure (UC-AP-05) throws BEFORE commit and rolls the approval back too.
        await _submit.ExecuteAsync(asn, now, request.OverrideReason, ct);

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
