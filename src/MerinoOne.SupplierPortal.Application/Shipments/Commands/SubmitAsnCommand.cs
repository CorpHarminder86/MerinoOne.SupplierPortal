using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Documents;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Documents;
using MerinoOne.SupplierPortal.Application.Invoices;
using MerinoOne.SupplierPortal.Application.Shipments.Policies;
using MerinoOne.SupplierPortal.Application.SystemSettings.Fulfilment;
using MerinoOne.SupplierPortal.Contracts.Shipments;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using ConflictException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ConflictException;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Shipments.Commands;

/// <summary>
/// R4 (2026-06-22) — Module 3, the core ASN orchestration. On submit (Draft -> Submitted) this, in ONE
/// transaction:
/// <list type="number">
///   <item>asserts Draft (else 409); validates over-ship, lot/serial (per Item flags), and the mixed-currency
///         guard R16 (all covered POs share one currency);</item>
///   <item>flips to Submitted, stamps submittedAt/by; from here Update + attachment-mutation are LOCKED;</item>
///   <item>stamps the ASN's <c>ErpSyncId</c> = the deterministic outbox key (the ERP correlation id);</item>
///   <item>creates EXACTLY ONE draft <see cref="Domain.Entities.Proc.Invoice"/> spanning all the ASN's POs via
///         <see cref="DraftInvoiceFromAsnFactory"/> (UQ_Invoice_asnId upsert-or-skip);</item>
///   <item>enqueues the ASN->ERP post on the Increment-0 outbox (post-commit dispatch; the draft invoice is
///         portal-internal and NOT gated on the LN result).</item>
/// </list>
/// ASN status + junction (unchanged) + draft Invoice + Outbox row all commit in ONE <c>SaveChangesAsync</c>.
/// </summary>
// R4 (2026-06-26) — §6.5 / UC-PO-09: OverrideReason carries the optional admin gate-override reason on submit.
// R4 (2026-06-26) — Phase 4 / §8.3 / UC-ATT-03: AcknowledgeMissingAttachments confirms proceeding past a missing
// Warning-level attachment. Returns SubmitOutcome<AsnDetailDto> so the Warning "confirm to proceed" path can be
// modelled WITHOUT throwing (Mandatory-missing still throws ValidationException → 400).
public record SubmitAsnCommand(Guid Id, string? OverrideReason = null, bool AcknowledgeMissingAttachments = false)
    : IRequest<SubmitOutcome<AsnDetailDto>>;

public class SubmitAsnCommandValidator : AbstractValidator<SubmitAsnCommand>
{
    public SubmitAsnCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}

public class SubmitAsnCommandHandler : IRequestHandler<SubmitAsnCommand, SubmitOutcome<AsnDetailDto>>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IOutboxDispatcher _outbox;
    private readonly DraftInvoiceFromAsnFactory _invoiceFactory;
    private readonly AttachmentSubmitGuard _attachmentGuard;
    private readonly IFulfilmentSettings _fulfilment;

    public SubmitAsnCommandHandler(
        IAppDbContext db, ICurrentUser user, IOutboxDispatcher outbox, DraftInvoiceFromAsnFactory invoiceFactory,
        AttachmentSubmitGuard attachmentGuard, IFulfilmentSettings fulfilment)
    {
        _db = db; _user = user; _outbox = outbox; _invoiceFactory = invoiceFactory; _attachmentGuard = attachmentGuard;
        _fulfilment = fulfilment;
    }

    public async Task<SubmitOutcome<AsnDetailDto>> Handle(SubmitAsnCommand request, CancellationToken ct)
    {
        var asn = await _db.Asns.FirstOrDefaultAsync(a => a.Id == request.Id, ct)
                  ?? throw new NotFoundException("Asn", request.Id);

        if (asn.AsnStatus != AsnStatus.Draft)
            throw new ConflictException($"ASN is '{asn.AsnStatus}'; only a Draft ASN can be submitted.");

        var now = DateTime.UtcNow;

        // ---- Validation: lines + serial/lot + over-ship -------------------------------------------------
        var lineCtx = await (from al in _db.AsnLines
                             join pol in _db.PurchaseOrderLines on al.PurchaseOrderLineId equals pol.Id
                             where al.AsnId == asn.Id
                             select new
                             {
                                 al.Id,
                                 al.ShippedQty,
                                 al.BatchNumber,
                                 al.PositionNo,
                                 pol.OrderQty,
                                 pol.ItemCode,
                                 pol.ItemId,
                             }).ToListAsync(ct);

        if (lineCtx.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["lines"] = new[] { "Cannot submit an ASN with no lines." }
            });

        // Item control flags (Addendum A3) for lot/serial validation. Resolve by **ItemCode within the ASN's
        // company** (NOT ItemId — PO lines are ERP-fed and routinely carry a null ItemId; ItemCode is always set,
        // and Item's natural key is (TenantEntityId, Code)). IgnoreQueryFilters — Item is company-scoped.
        var asnCompany = asn.TenantEntityId;
        var itemCodes = lineCtx.Select(l => l.ItemCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var itemRows = await _db.Items.IgnoreQueryFilters()
            .Where(i => i.TenantEntityId == asnCompany && !i.IsDeleted && itemCodes.Contains(i.Code))
            .Select(i => new { i.Code, i.IsLotControlled, i.IsSerialized })
            .ToListAsync(ct);
        var itemFlags = itemRows.ToDictionary(
            i => i.Code, i => (i.IsLotControlled, i.IsSerialized), StringComparer.OrdinalIgnoreCase);

        // R4 (2026-06-23) — Serial/Lot capture children for this ASN's lines (grouped by line id).
        var thisLineIds = lineCtx.Select(l => l.Id).ToList();
        var serialsByLine = (await _db.AsnLineSerials
                .Where(s => thisLineIds.Contains(s.AsnLineId))
                .Select(s => new { s.AsnLineId, s.SerialNumber })
                .ToListAsync(ct))
            .GroupBy(s => s.AsnLineId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.SerialNumber).ToList());
        var lotsByLine = (await _db.AsnLineLots
                .Where(l => thisLineIds.Contains(l.AsnLineId))
                .Select(l => new { l.AsnLineId, l.LotNo, l.Qty })
                .ToListAsync(ct))
            .GroupBy(l => l.AsnLineId)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.LotNo, x.Qty)).ToList());

        // Covered PO id(s) for the cross-ASN uniqueness scope: the junction rows + the legacy scalar header PO.
        var coveredPoIds = await _db.AsnPurchaseOrders
            .Where(j => j.AsnId == asn.Id && !j.IsDeleted)
            .Select(j => j.PurchaseOrderId)
            .ToListAsync(ct);
        var coveredPoSet = coveredPoIds.ToHashSet();
        if (asn.PurchaseOrderId.HasValue) coveredPoSet.Add(asn.PurchaseOrderId.Value);
        var poIdList = coveredPoSet.ToList();

        // R4 (2026-06-26) — Addendum §6.2 / §6.5, Component 3 (PO Confirmation Gate). Re-evaluate the ship-gate per
        // covered PO on Draft → Submitted (a mid-fulfilment ERP Modify can have reset a covered PO to Released since
        // the draft was saved — UC-ASN-08). This gates ONLY this Draft submission; already-Submitted/InTransit ASNs
        // are never re-blocked (this handler asserts Draft up front — UC-ASN-09). Admin override (UC-PO-09): a caller
        // holding PurchaseOrder.OverrideGate with a non-empty OverrideReason proceeds + writes an audited row.
        var coveredPos = await _db.PurchaseOrders.Where(p => poIdList.Contains(p.Id)).ToListAsync(ct);
        var confirmationMode = await _db.Suppliers.Where(s => s.Id == asn.SupplierId)
            .Select(s => s.PoConfirmationMode).FirstOrDefaultAsync(ct);
        PoConfirmationGateEnforcer.Enforce(
            _db, coveredPos, confirmationMode, request.OverrideReason, _user, asn.Id, asn.AsnNumber, now);

        var errors = new Dictionary<string, List<string>>();
        void AddErr(string key, string msg)
        {
            if (!errors.TryGetValue(key, out var list)) { list = new(); errors[key] = list; }
            list.Add(msg);
        }

        // Collect this ASN's serials / lotNos across all lines for the intra-ASN duplicate check.
        var allSerials = new List<string>();
        var allLotNos = new List<string>();

        // ── Serial / Lot count + content validation (per line) ──────────────────────────────────────────
        foreach (var l in lineCtx)
        {
            if (string.IsNullOrWhiteSpace(l.ItemCode) || !itemFlags.TryGetValue(l.ItemCode, out var flags)) continue;

            if (flags.IsSerialized)
            {
                var serials = serialsByLine.TryGetValue(l.Id, out var sl) ? sl : new List<string>();

                // ShippedQty must be a whole number and the serial count must match it exactly.
                if (l.ShippedQty != Math.Truncate(l.ShippedQty))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is serialized; ShippedQty must be a whole number.");
                else if (serials.Count != (int)l.ShippedQty)
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is serialized; expected {(int)l.ShippedQty} serial(s) but got {serials.Count}.");

                if (serials.Any(string.IsNullOrWhiteSpace))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) has an empty serial number.");

                allSerials.AddRange(serials.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
            }
            else if (flags.IsLotControlled)
            {
                var lots = lotsByLine.TryGetValue(l.Id, out var ll) ? ll : new List<(string LotNo, decimal Qty)>();

                if (lots.Count == 0)
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is lot-controlled; at least one lot is required.");

                if (lots.Any(x => string.IsNullOrWhiteSpace(x.LotNo)))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) has a lot with an empty lot number.");
                if (lots.Any(x => x.Qty <= 0))
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) has a lot with a non-positive quantity.");

                var lotSum = lots.Sum(x => x.Qty);
                if (lotSum != l.ShippedQty)
                    AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) is lot-controlled; Σ(lot qty) {lotSum} must equal ShippedQty {l.ShippedQty}.");

                allLotNos.AddRange(lots.Where(x => !string.IsNullOrWhiteSpace(x.LotNo)).Select(x => x.LotNo.Trim()));
            }
        }

        // ── Intra-ASN uniqueness: a serial / lotNo may appear only once across this ASN's lines ──────────
        foreach (var dup in allSerials.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key))
            AddErr("lines", $"Serial number '{dup}' appears more than once on this ASN.");
        foreach (var dup in allLotNos.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key))
            AddErr("lines", $"Lot number '{dup}' appears more than once on this ASN.");

        // ── Cross-ASN uniqueness within the same PO(s): reject a serial / lotNo already captured on any other
        //    non-deleted ASN line whose ASN covers one of this ASN's PO(s). The PO scope is resolved via the
        //    AsnPurchaseOrder junction OR the legacy scalar Asn.PurchaseOrderId (single-PO back-compat). ──────
        if (poIdList.Count > 0)
        {
            // Other ASN-line ids that cover the same PO(s) (excluding THIS ASN). Union of junction-covered and
            // legacy-scalar-covered ASNs, then their non-deleted lines.
            var otherAsnIdsViaJunction = _db.AsnPurchaseOrders
                .Where(j => !j.IsDeleted && j.AsnId != asn.Id && poIdList.Contains(j.PurchaseOrderId))
                .Select(j => j.AsnId);
            var otherAsnIdsViaScalar = _db.Asns
                .Where(a => a.Id != asn.Id && !a.IsDeleted && a.PurchaseOrderId != null && poIdList.Contains(a.PurchaseOrderId.Value))
                .Select(a => a.Id);
            var otherLineIds = _db.AsnLines
                .Where(al => !al.IsDeleted
                             && (otherAsnIdsViaJunction.Contains(al.AsnId) || otherAsnIdsViaScalar.Contains(al.AsnId)))
                .Select(al => al.Id);

            if (allSerials.Count > 0)
            {
                var clash = await _db.AsnLineSerials
                    .Where(s => otherLineIds.Contains(s.AsnLineId) && allSerials.Contains(s.SerialNumber))
                    .Select(s => s.SerialNumber)
                    .Distinct()
                    .ToListAsync(ct);
                foreach (var s in clash)
                    AddErr("lines", $"Serial number '{s}' is already used on another ASN for the same PO.");
            }

            if (allLotNos.Count > 0)
            {
                var clash = await _db.AsnLineLots
                    .Where(l => otherLineIds.Contains(l.AsnLineId) && allLotNos.Contains(l.LotNo))
                    .Select(l => l.LotNo)
                    .Distinct()
                    .ToListAsync(ct);
                foreach (var l in clash)
                    AddErr("lines", $"Lot number '{l}' is already used on another ASN for the same PO.");
            }
        }

        // ── Over-ship (run AFTER serial/lot so a count mismatch surfaces first) ──────────────────────────
        foreach (var l in lineCtx)
        {
            if (l.OrderQty > 0 && l.ShippedQty > l.OrderQty)
                AddErr("lines", $"Line '{l.ItemCode}' (pos {l.PositionNo}) ships {l.ShippedQty} > ordered {l.OrderQty} (over-ship).");
        }

        if (errors.Count > 0)
            throw new ValidationException(errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray()));

        // ---- Attachment Requirement Governance (Phase 4 / §8.3, UC-ATT-01..05) --------------------------
        // Evaluate the active policy (entity "Asn", supplier = this ASN's supplier) AFTER all line validation but
        // BEFORE the state flip. Mandatory-missing throws (400). Warning-missing + not-acknowledged returns the
        // ConfirmationRequired outcome WITHOUT mutating the ASN. Acknowledged-skip stages a skip AuditEntry that
        // commits in this handler's SaveChangesAsync (same transaction).
        var attachmentDecision = await _attachmentGuard.EvaluateAsync(
            _db, DocumentOwnerTypes.Asn, asn.Id, asn.AsnNumber, asn.SupplierId,
            request.AcknowledgeMissingAttachments, asn.TenantId, now, ct);
        if (attachmentDecision.RequiresConfirmation)
            return SubmitOutcome<AsnDetailDto>.Confirm(attachmentDecision.MissingWarning);

        // ---- Flip to Submitted + stamp + ERP correlation key --------------------------------------------
        // deterministic — reused across retries; tenant-qualified (review B2).
        var key = OutboxKey.For(OutboxEntity.Asn, asn.TenantId, asn.AsnNumber, "submit");
        asn.AsnStatus = AsnStatus.Submitted;
        asn.SubmittedAt = now;
        asn.SubmittedBy = _user.UserCode;
        asn.ErpSyncId = key;
        asn.UpdatedBy = _user.UserCode;
        asn.UpdatedOn = now;

        // ---- Single draft invoice spanning all the ASN's POs (R16 currency guard inside; upsert-or-skip) -
        await _invoiceFactory.EnsureDraftAsync(asn, now, ct);

        // ---- Outbox: ASN -> ERP post, dispatched POST-COMMIT (never an LN HTTP call inside this txn) ------
        await _outbox.EnqueueAsync(OutboxTransactionType.AsnPost, OutboxEntity.Asn, asn.Id, key, null, ct);

        // ONE transaction: ASN status + draft Invoice (+ lines) + Outbox row + any attachment-skip audit.
        await _db.SaveChangesAsync(ct);

        var dto = await AsnDtoBuilder.BuildAsync(_db, asn.Id, ct, _fulfilment.OverShipAllowanceRounding);
        return SubmitOutcome<AsnDetailDto>.Completed(dto);
    }
}
