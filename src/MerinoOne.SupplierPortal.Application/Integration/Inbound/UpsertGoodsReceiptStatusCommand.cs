using System.Text.Json;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Audit;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// R4 (2026-06-22) — Module 5 / Increment D. THE financial-correctness command: the GRN-status inbound webhook
/// (LN pushes GRN approval state) plus the invoice auto-post cascade. A double-post here pays a supplier twice,
/// so the idempotency guards are non-negotiable.
///
/// <para><b>Upsert.</b> Runs through <see cref="InboundUpsertExecutor"/> (company resolution, anti-spoof,
/// endpoint gate, canonical-hash idempotency, transactional SyncLog/IntegrationError, endpoint-session
/// telemetry). GRN rows are matched by <c>grnNumber</c> within the resolved company; the status is updated and
/// the deterministic <c>GoodsReceipt.InvoiceId</c> link is stamped from the GRN's ASN (<c>GoodsReceipt.AsnId</c>
/// → the Invoice with that <c>asnId</c>) — NO fuzzy/string match; null invoice ⇒ left null.</para>
///
/// <para><b>Auto-post cascade — THREE independent idempotency guards (ALL required):</b> the invoice post fires
/// ONLY when (a) a GRN <i>transitions into</i> <see cref="GrnStatus.GrnApproved"/> (tracked on the row — never
/// on re-seeing an already-approved row), AND (b) ALL GRN lines covering that invoice's PO lines are
/// <see cref="GrnStatus.GrnApproved"/> (<see cref="AllCoveringGrnsApprovedAsync"/> via the InvoiceId FK,
/// tenant-scoped — review N1), AND (c) the post is won by an ATOMIC CLAIM (review S1): a single conditional
/// <c>ExecuteUpdateAsync</c> stamps <c>erpPostInitiatedAt = now WHERE invoiceStatus = Submitted AND
/// erpPostInitiatedAt IS NULL</c> and the post is enqueued ONLY when that affected exactly one row — so a
/// concurrent/duplicate webhook that already initiated the post loses the claim and is skipped (no double-post, no
/// batch-nuking exception). The post is enqueued on the outbox with the TENANT+SUPPLIER-qualified deterministic
/// key <c>sha256("&lt;tenantId&gt;|Invoice|&lt;supplierId&gt;|&lt;invoiceNumber&gt;|post")</c> (review B2; the worker calls
/// SubmitInvoiceAsync; Q-LN confirmed LN dedupes on the business key — the key is defence-in-depth). A system-actor
/// AuditEntry (CreatedBy="system:grn-autopost") referencing the triggering GRN is written. Partial coverage ⇒ NO
/// post.</para>
///
/// <para><b>Review S2 — initiated vs posted.</b> The claim stamps <c>erpPostInitiatedAt</c> ("post initiated",
/// gates re-enqueue). <c>erpPostedAt</c> ("post genuinely landed in ERP") is set LATER by the dispatcher on a
/// confirmed <c>Dispatched</c> success and/or by <c>/inbound/erp-ack</c> for the InvoicePost. A dispatch FAILURE
/// leaves <c>erpPostedAt</c> null so the invoice remains re-postable (the old single-marker design stranded it).</para>
///
/// <para><b>Reverse transition.</b> <see cref="GrnStatus.GrnApproved"/> → NotApproved/Rejected from an LN
/// correction updates the status; if the invoice was already posted (<c>erpPostedAt</c> set) it raises an
/// operator alert / IntegrationError — NO auto un-post (out of scope, risk-managed).</para>
/// </summary>
public record UpsertGoodsReceiptStatusCommand(
    PushGrnStatusRequest Body,
    IReadOnlySet<Guid> BoundCompanyIds,
    string? IdempotencyKey) : IRequest<UpsertGrnStatusResultDto>;

public class UpsertGoodsReceiptStatusCommandValidator : AbstractValidator<UpsertGoodsReceiptStatusCommand>
{
    private static readonly HashSet<string> ValidStatuses =
        Enum.GetNames<GrnStatus>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    public UpsertGoodsReceiptStatusCommandValidator()
    {
        RuleFor(x => x.Body.CompanyCode).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Body.Receipts).NotEmpty().Must(r => r == null || r.Count <= 1000)
            .WithMessage("Between 1 and 1000 receipts per batch.");
        RuleForEach(x => x.Body.Receipts).ChildRules(r =>
        {
            r.RuleFor(x => x.GrnNumber).NotEmpty().MaximumLength(100);
            r.RuleFor(x => x.GrnStatus).NotEmpty()
                .Must(s => s != null && ValidStatuses.Contains(s.Trim()))
                .WithMessage(_ => $"Unknown grnStatus. Allowed: {string.Join(", ", Enum.GetNames<GrnStatus>())}.");
        });
    }
}

public class UpsertGoodsReceiptStatusCommandHandler(InboundUpsertExecutor exec, IOutboxDispatcher outbox)
    : IRequestHandler<UpsertGoodsReceiptStatusCommand, UpsertGrnStatusResultDto>
{
    private const string AutoPostActor = "system:grn-autopost";

    public async Task<UpsertGrnStatusResultDto> Handle(UpsertGoodsReceiptStatusCommand request, CancellationToken ct)
    {
        var recs = request.Body.Receipts;

        // Canonical projection for the executor's payload-hash idempotency (replay short-circuit).
        var canonical = recs.Select(r =>
            $"{r.GrnNumber.Trim().ToUpperInvariant()}|{r.GrnStatus.Trim()}|{r.ReceivedQty}|{(r.AsnNumber ?? r.AsnErpRef ?? "").Trim()}|{(r.ErpSyncId ?? "").Trim()}|{(r.ErpCode ?? "").Trim()}");
        var codes = recs.Select(r => r.GrnNumber.Trim());

        // Cascade telemetry collected inside the transactional callback.
        var rich = new List<GrnStatusRowResult>(recs.Count);

        var result = await exec.ExecuteAsync(
            TransactionalInboundEntity.Grn, request.Body.CompanyCode, request.BoundCompanyIds, request.IdempotencyKey,
            recs.Count, canonical, codes, request.Body, Upsert, ct);

        // Replay short-circuit (prior-Success): the executor returns all-Skipped with an empty Rows list.
        if (rich.Count == 0)
            rich = result.Rows
                .Select(r => new GrnStatusRowResult(r.Code, r.Outcome, r.Error))
                .ToList();

        return new UpsertGrnStatusResultDto(
            result.CompanyCode, result.Received, result.Inserted, result.Updated, result.Skipped, result.Failed,
            AutoPostsEnqueued: rich.Count(r => r.AutoPostEnqueued),
            ReverseTransitionAlerts: rich.Count(r => r.ReverseTransitionAlert),
            Rows: rich);

        // ----------------------------------------------------------------------------------------------------
        // Transactional upsert + cascade. Runs INSIDE the executor's transaction so the GRN status change, the
        // GoodsReceipt.InvoiceId link, the Invoice.erpPostedAt stamp, the AuditEntry and the Outbox row ALL
        // commit (or roll back) together. sourceId = the resolved company (TenantEntityId).
        // ----------------------------------------------------------------------------------------------------
        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, Guid sourceId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);

            // Load the GRN rows to be touched by grnNumber within the resolved company. IgnoreQueryFilters —
            // the service principal has no seccode/company context; restrict by tenant + company explicitly.
            var grnNumbers = recs.Select(r => r.GrnNumber.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var grns = await db.GoodsReceipts.IgnoreQueryFilters()
                .Where(g => !g.IsDeleted && g.TenantId == tenantId && g.TenantEntityId == sourceId && grnNumbers.Contains(g.GrnNumber))
                .ToListAsync(token);
            // A GRN created via /goods-receipts has ONE row per PO position, so a GrnNumber maps to MANY rows.
            // Group (not ToDictionary-by-key, which threw "same key already added" on multi-line GRNs).
            var grnByNumber = grns.GroupBy(g => g.GrnNumber, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // ASN resolution for the deterministic GRN→Invoice link (by AsnNumber or AsnErpRef/ErpSyncId).
            var asnKeys = recs
                .Select(r => (r.AsnNumber ?? r.AsnErpRef)?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var asnByKey = asnKeys.Count == 0
                ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
                : (await db.Asns.IgnoreQueryFilters()
                        .Where(a => !a.IsDeleted && a.TenantId == tenantId && a.TenantEntityId == sourceId
                                    && (asnKeys.Contains(a.AsnNumber) || (a.ErpSyncId != null && asnKeys.Contains(a.ErpSyncId))))
                        .Select(a => new { a.Id, a.AsnNumber, a.ErpSyncId })
                        .ToListAsync(token))
                    .SelectMany(a => new[]
                    {
                        new KeyValuePair<string, Guid>(a.AsnNumber, a.Id),
                        a.ErpSyncId != null ? new KeyValuePair<string, Guid>(a.ErpSyncId, a.Id) : default
                    })
                    .Where(kv => !string.IsNullOrEmpty(kv.Key))
                    .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

            // Track invoices whose coverage we should re-evaluate (set, so each is checked at most once even if
            // several lines of the same invoice transition in one batch).
            var candidateInvoiceByGrn = new Dictionary<string, (Guid invoiceId, Guid triggeringGrnId)>(StringComparer.OrdinalIgnoreCase);

            foreach (var rec in recs)
            {
                var code = rec.GrnNumber.Trim();
                var newStatus = Enum.Parse<GrnStatus>(rec.GrnStatus.Trim(), ignoreCase: true);

                if (!grnByNumber.TryGetValue(code, out var grnRows) || grnRows.Count == 0)
                {
                    // GRNs originate in the portal (created via the GRN ingest / seed against a PO line). A
                    // status push for an unknown GRN is a hard failure — we do NOT invent a GoodsReceipt with a
                    // phantom PurchaseOrderLineId/seccode (that would corrupt the auto-post coverage check).
                    results.Add(new RowResult(code, RowOutcome.Failed, $"No goods-receipt '{code}' for the resolved company."));
                    rich.Add(new GrnStatusRowResult(code, RowOutcome.Failed, $"No goods-receipt '{code}' for the resolved company."));
                    continue;
                }

                // Apply the status to EVERY row sharing this GrnNumber (one row per PO position). They move together.
                var reverseAlert = false;
                foreach (var grn in grnRows)
                {
                    var oldStatus = grn.GrnStatus;

                    // Deterministically resolve + stamp the GRN→Invoice link from the GRN's ASN. Prefer the row's own
                    // AsnId; else resolve from the pushed AsnNumber/AsnErpRef. Then map AsnId → the Invoice on that ASN.
                    if (grn.AsnId is null)
                    {
                        var asnKey = (rec.AsnNumber ?? rec.AsnErpRef)?.Trim();
                        if (!string.IsNullOrWhiteSpace(asnKey) && asnByKey.TryGetValue(asnKey, out var resolvedAsnId))
                            grn.AsnId = resolvedAsnId;
                    }
                    if (grn.InvoiceId is null && grn.AsnId is not null)
                    {
                        var invId = await db.Invoices.IgnoreQueryFilters()
                            .Where(i => !i.IsDeleted && i.AsnId == grn.AsnId)
                            .Select(i => (Guid?)i.Id)
                            .FirstOrDefaultAsync(token);
                        if (invId is not null) grn.InvoiceId = invId;
                    }

                    // Apply the status + ERP-fed remark/code + optional receivedQty.
                    grn.GrnStatus = newStatus;
                    if (rec.ReceivedQty.HasValue) grn.ReceivedQty = rec.ReceivedQty.Value;
                    if (!string.IsNullOrWhiteSpace(rec.IssueReported)) grn.IssueReported = rec.IssueReported.Trim();
                    if (!string.IsNullOrWhiteSpace(rec.ErpCode)) grn.ErpCode = rec.ErpCode.Trim();
                    if (!string.IsNullOrWhiteSpace(rec.ErpSyncId)) grn.ErpSyncId = rec.ErpSyncId.Trim();
                    grn.UpdatedBy = "infor:inbound";
                    grn.UpdatedOn = now;

                    // GUARD (a) — transition-tracking. The cascade is considered ONLY on a real transition INTO
                    // GrnApproved; re-seeing an already-approved row (oldStatus == GrnApproved) does nothing.
                    var transitionedIntoApproved = oldStatus != GrnStatus.GrnApproved && newStatus == GrnStatus.GrnApproved;
                    if (transitionedIntoApproved)
                    {
                        grn.GrnApprovedAt = now;
                        if (grn.InvoiceId is Guid invForGrn)
                            candidateInvoiceByGrn[code] = (invForGrn, grn.Id);
                    }
                    else if (newStatus != GrnStatus.GrnApproved)
                    {
                        // Reverse transition (GrnApproved → NotApproved/Rejected) from an LN correction.
                        grn.GrnApprovedAt = null;
                    }

                    // Reverse-transition alert: an already-POSTED invoice (S2: erpPostedAt = the post genuinely landed
                    // in ERP, not merely initiated) whose covering GRN just left GrnApproved. A merely-initiated post
                    // that has not yet landed does NOT alert here — its dispatch can still resolve. Tenant-scoped.
                    if (oldStatus == GrnStatus.GrnApproved && newStatus != GrnStatus.GrnApproved && grn.InvoiceId is Guid revInvId)
                    {
                        var posted = await db.Invoices.IgnoreQueryFilters()
                            .AnyAsync(i => i.Id == revInvId && i.TenantId == tenantId && i.ErpPostedAt != null, token);
                        if (posted)
                        {
                            reverseAlert = true;
                            RaiseReverseTransitionAlert(db, tenantId, revInvId, grn, now);
                        }
                    }
                }

                results.Add(new RowResult(code, RowOutcome.Updated, null));
                rich.Add(new GrnStatusRowResult(code, RowOutcome.Updated, null,
                    AutoPostEnqueued: false, ReverseTransitionAlert: reverseAlert));
            }

            // Auto-post cascade for the invoices whose coverage may now be complete. Distinct invoiceId — even if
            // several of its GRN lines transitioned in this batch, we evaluate + enqueue at most once.
            var seenInvoices = new HashSet<Guid>();
            foreach (var (grnCode, candidate) in candidateInvoiceByGrn)
            {
                if (!seenInvoices.Add(candidate.invoiceId)) continue;

                // GUARD (b) — all-covering-GRNs-approved (via the InvoiceId FK), tenant-scoped (review N1).
                if (!await AllCoveringGrnsApprovedAsync(db, tenantId, sourceId, candidate.invoiceId, token)) continue;

                // Read the invoice fields needed to build the tenant+supplier-qualified key and the audit row.
                // Tenant-scoped (defence-in-depth: this is the same scope as the S1 claim below).
                var inv = await db.Invoices.IgnoreQueryFilters()
                    .Where(i => i.Id == candidate.invoiceId && !i.IsDeleted && i.TenantId == tenantId)
                    .Select(i => new { i.Id, i.InvoiceNumber, i.SupplierId, i.TenantId })
                    .FirstOrDefaultAsync(token);
                if (inv is null) continue;

                // GUARD (c) + S1 — ATOMIC post claim. Instead of read-then-write under RowVersion (which 500s the
                // whole batch on a concurrency clash), gate the post with a single conditional server-side update:
                // stamp erpPostInitiatedAt ONLY when it is still NULL AND the invoice is Submitted. We enqueue the
                // outbox post ONLY when this affected exactly one row. A concurrent/duplicate GRN webhook that
                // already initiated the post loses the claim (rowcount==0) and is skipped — no double-post, no
                // batch-nuking exception. The claim runs inside the executor's transaction, so it commits/rolls
                // back atomically with the GRN status changes, the outbox row and the audit entry.
                //
                // S2 — this stamps erpPostInitiatedAt ("post initiated"), NOT erpPostedAt ("post landed").
                // erpPostedAt is set later by the dispatcher on a confirmed Dispatched/erp-ack; a dispatch FAILURE
                // therefore leaves the invoice re-postable (a future GRN re-approval re-claims it once a manual
                // retry/operator clears erpPostInitiatedAt, or the dispatcher's retry path replays the outbox row).
                var claimed = await db.Invoices
                    .IgnoreQueryFilters()
                    .Where(i => i.Id == inv.Id
                                && i.TenantId == tenantId
                                && i.InvoiceStatus == InvoiceStatus.Submitted
                                && i.ErpPostInitiatedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.ErpPostInitiatedAt, now)
                        .SetProperty(i => i.UpdatedBy, AutoPostActor)
                        .SetProperty(i => i.UpdatedOn, now), token);
                if (claimed != 1) continue;

                // Deterministic outbox key — REUSED across retries so LN dedupes (defence-in-depth on top of LN's
                // own business-key dedup, Q-LN). TENANT + SUPPLIER-qualified (review B2): InvoiceNumber is unique
                // only per (SupplierId, InvoiceNumber) within a tenant, so the key folds both. Doubles as the ERP
                // correlation id / portalRef echoed back on /inbound/erp-ack — MUST be tenant-unique.
                var key = OutboxKey.For(OutboxEntity.Invoice, inv.TenantId, $"{inv.SupplierId:N}|{inv.InvoiceNumber}", "post");

                // Stamp erpSyncId (the correlation key) alongside the claim, in the SAME transaction as the enqueue.
                await db.Invoices
                    .IgnoreQueryFilters()
                    .Where(i => i.Id == inv.Id && i.TenantId == tenantId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.ErpSyncId, key), token);

                await outbox.EnqueueAsync(OutboxTransactionType.InvoicePost, OutboxEntity.Invoice, inv.Id, key, null, token);

                // System-actor audit referencing the triggering GRN — money-movement automation must be legible.
                // Operation MUST be one of Insert/Update/Delete (CK_AuditEntry_operation): this row records the
                // erpPostInitiatedAt field update, so it is an "Update". The auto-post intent is carried verbatim
                // in NewValue below — a non-conforming Operation (e.g. "AutoPost") fails the CHECK constraint and
                // 500s the whole cascade SaveChanges.
                db.AuditEntries.Add(new AuditEntry
                {
                    Id = Guid.NewGuid(),
                    EntityName = nameof(Domain.Entities.Proc.Invoice),
                    EntityId = inv.Id,
                    Operation = "Update",
                    FieldName = nameof(Domain.Entities.Proc.Invoice.ErpPostInitiatedAt),
                    OldValue = null,
                    NewValue = $"GRN-approved auto-post enqueued (key={key}; trigger GRN={candidate.triggeringGrnId})",
                    ChangedBy = AutoPostActor,
                    ChangedOn = now,
                });

                // Flag the triggering receipt's rich result as having enqueued the post.
                var idx = rich.FindIndex(r => string.Equals(r.GrnNumber, grnCode, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) rich[idx] = rich[idx] with { AutoPostEnqueued = true };
            }

            return results;
        }
    }

    /// <summary>
    /// GUARD (b). True only when the invoice has at least one linked GRN AND every (non-deleted) GRN linked to
    /// it via the <c>GoodsReceipt.InvoiceId</c> FK is <see cref="GrnStatus.GrnApproved"/>. Partial coverage
    /// (any linked GRN still NotApproved/Rejected) ⇒ false ⇒ NO post.
    ///
    /// <para>CRITICAL: the GRN status changes in this batch are NOT yet saved (the executor commits once at the
    /// end), so a raw SQL count would see the PRE-batch statuses and never observe the just-applied approval that
    /// completes the set. The coverage is therefore computed from the change-tracker's <c>Local</c> view (the
    /// in-flight tracked rows, with their updated statuses) UNIONed with the DB rows not loaded in this batch.</para>
    ///
    /// <para>Review N1 — the coverage read is TENANT-SCOPED: the DB-side covering-GRN query filters
    /// <c>g.TenantId == tenantId &amp;&amp; g.TenantEntityId == sourceId</c>, reading under the SAME scope as the S1
    /// post claim and the rest of this handler (every other GRN/ASN/Invoice read here is scoped the same way). The
    /// change-tracker <c>Local</c> merge is preserved (the in-flight rows were themselves loaded under that scope).</para>
    /// </summary>
    private static async Task<bool> AllCoveringGrnsApprovedAsync(
        IAppDbContext db, Guid tenantId, Guid sourceId, Guid invoiceId, CancellationToken ct)
    {
        // In-flight tracked GRNs for this invoice (their statuses reflect the mutations applied above). These were
        // loaded under the tenant+company scope, but re-assert it on the Local view for symmetry with the DB read.
        var localForInvoice = db.GoodsReceipts.Local
            .Where(g => !g.IsDeleted && g.InvoiceId == invoiceId
                        && g.TenantId == tenantId && g.TenantEntityId == sourceId)
            .ToList();
        var localIds = localForInvoice.Select(g => g.Id).ToHashSet();

        // DB rows linked to the invoice that are NOT tracked locally (untouched by this batch). Read only the
        // status; tracked rows are excluded so their stale DB status doesn't shadow the in-flight value. Tenant +
        // company scoped (review N1).
        var dbRows = await db.GoodsReceipts.IgnoreQueryFilters()
            .Where(g => !g.IsDeleted && g.InvoiceId == invoiceId
                        && g.TenantId == tenantId && g.TenantEntityId == sourceId
                        && !localIds.Contains(g.Id))
            .Select(g => g.GrnStatus)
            .ToListAsync(ct);

        var total = localForInvoice.Count + dbRows.Count;
        if (total == 0) return false;   // no covering GRN ⇒ never post.

        var approved = localForInvoice.Count(g => g.GrnStatus == GrnStatus.GrnApproved)
                       + dbRows.Count(s => s == GrnStatus.GrnApproved);

        return total == approved;
    }

    /// <summary>
    /// Reverse-transition handling for an already-posted invoice: NO auto un-post (out of scope). Record an
    /// unresolved IntegrationError so an operator reconciles the LN reversal manually.
    /// </summary>
    private static void RaiseReverseTransitionAlert(IAppDbContext db, Guid tenantId, Guid invoiceId, Domain.Entities.Proc.GoodsReceipt grn, DateTime now)
    {
        db.IntegrationErrors.Add(new IntegrationError
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EntityName = nameof(TransactionalInboundEntity.Grn),
            ErrorMessage =
                $"GRN '{grn.GrnNumber}' reverted from GrnApproved to '{grn.GrnStatus}' but invoice {invoiceId} is already posted to ERP. " +
                "Auto un-post is out of scope — reconcile the ERP reversal manually.",
            StackTrace = JsonSerializer.Serialize(new { grnId = grn.Id, grn.GrnNumber, invoiceId, newStatus = grn.GrnStatus.ToString() }),
            RetryCount = 0,
            IsResolved = false,
            CreatedBy = AutoPostActor,
            CreatedOn = now,
        });
    }
}
