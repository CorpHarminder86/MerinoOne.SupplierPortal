using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Documents;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

// ============================================================================================================
// R14 (2026-08-05) — POST: the supplier's final action and the ONLY path that reaches the ERP.
//
// This handler is where the two guards that R5 put on Send-For-Approval now live, and where the submit path
// that R5 put on Approve now lives:
//   • mandatory shipment references (R11 D6/D7) — moved here from Send-For-Approval;
//   • attachment-requirement governance (R4 §8.3) — moved here from Send-For-Approval;
//   • AsnSubmitExecutor (balance consumption, draft invoice, LN outbox) — moved here from Approve.
//
// From-states: Approved (both modes) or Draft (only when the supplier needs no buyer confirmation).
// ============================================================================================================

/// <summary>
/// R14 — Approved|Draft → Submitted. Enforces the shipment references and the attachment policy, stamps the
/// shipping date (<c>PostedAt</c>), notifies the mapped buyers, then runs
/// <see cref="AsnSubmitExecutor"/> to consume PO balance, generate the draft invoice and enqueue the LN post.
///
/// <para>Returns the two-step confirm outcome when a Warning-level attachment is missing and unacknowledged —
/// the same contract Send-For-Approval used to carry, so the existing client dialog works unchanged.</para>
/// </summary>
public record PostAsnCommand(Guid Id, bool AcknowledgeMissingAttachments = false, string? OverrideReason = null)
    : IRequest<SubmitOutcome<AsnDetailDto>>;

public class PostAsnCommandValidator : AbstractValidator<PostAsnCommand>
{
    public PostAsnCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class PostAsnCommandHandler : IRequestHandler<PostAsnCommand, SubmitOutcome<AsnDetailDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly AttachmentSubmitGuard _attachmentGuard;
    private readonly AsnSubmitExecutor _submit;
    private readonly IFulfilmentSettings _fulfilment;

    public PostAsnCommandHandler(
        IAppDbContext db, ICurrentUser user, AttachmentSubmitGuard attachmentGuard,
        AsnSubmitExecutor submit, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _attachmentGuard = attachmentGuard; _submit = submit; _fulfilment = fulfilment;
    }

    public async Task<SubmitOutcome<AsnDetailDto>> Handle(PostAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        var confirmationRequired = await _db.Suppliers
            .Where(s => s.Id == asn.SupplierId)
            .Select(s => s.AsnConfirmationRequired)
            .FirstOrDefaultAsync(ct);

        AsnLifecycle.AssertCanPost(asn.AsnStatus, confirmationRequired);

        // ---- R11 (D6/D7) shipment references — MANDATORY here (moved from Send-For-Approval) -------------
        // This is now the last point before the ASN leaves the supplier's hands: the LN payload forwards all
        // three verbatim, and nothing after this is editable. D8 — no grandfathering.
        var missingRefs = new List<string>();
        if (string.IsNullOrWhiteSpace(asn.InvoiceNo)) missingRefs.Add("invoice no.");
        if (string.IsNullOrWhiteSpace(asn.BillOfLading)) missingRefs.Add("bill of lading");
        if (string.IsNullOrWhiteSpace(asn.PackingList)) missingRefs.Add("packing list");
        if (missingRefs.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["shipmentReferences"] = new[]
                {
                    $"Fill in {string.Join(", ", missingRefs)} on the ASN before posting it."
                }
            });

        var now = DateTime.UtcNow;

        // ---- Attachment Requirement Governance (moved from Send-For-Approval, §10.3) ---------------------
        // Mandatory missing → throws (400). Warning missing + not-acknowledged → ConfirmationRequired (no
        // mutation). Acknowledged-skip stages a skip AuditEntry that commits with this handler's transaction.
        var decision = await _attachmentGuard.EvaluateAsync(
            _db, DocumentOwnerTypes.Asn, asn.Id, asn.AsnNumber, asn.SupplierId,
            request.AcknowledgeMissingAttachments, asn.TenantId, now, ct);
        if (decision.RequiresConfirmation)
            return SubmitOutcome<AsnDetailDto>.Confirm(decision.MissingWarning);

        // NOTE: AsnDraftGate is deliberately NOT called here. PO shippability is re-evaluated moments later by
        // PoConfirmationGateEnforcer inside the executor, and THAT path honours the §6.5 admin override
        // (PurchaseOrder.OverrideGate + reason, audited). The draft gate is a hard block with no override, so
        // running it first would make an ASN whose PO was reset by an ERP Modify unpostable by anyone.

        // ---- D9: the shipping date is the POST date ------------------------------------------------------
        asn.PostedAt = now;
        asn.PostedBy = _user.UserCode;

        // ---- D12: tell the mapped buyers the shipment has gone -------------------------------------------
        // Staged on the SAME context BEFORE the executor, so these rows commit inside the executor's
        // transaction and roll back with it. A post that fails the over-ship guard must not email "posted".
        var buyers = await AsnApprovalSupport.ResolveApproverUserIdsAsync(_db, asn, ct);
        await AsnApprovalSupport.NotifyBuyersPostedAsync(_db, asn, buyers.ToList(), now, ct);

        // ---- The submit path -----------------------------------------------------------------------------
        // Consumes balance via the atomic over-ship guard, flips the ASN to Submitted, stamps SubmittedAt with
        // this same `now` (so the LN payload's ShipmentDate IS the post date — no wire change needed, D9),
        // creates the grouped draft invoice(s) and enqueues the gated LN outbox message.
        await _submit.ExecuteAsync(asn, now, request.OverrideReason, ct);

        var dto = await AsnDtoBuilder.BuildAsync(_db, asn.Id, ct, _fulfilment.OverShipAllowanceRounding);
        return SubmitOutcome<AsnDetailDto>.Completed(dto);
    }
}
