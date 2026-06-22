using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// R4 (2026-06-22) — Module 5 / Increment D (the ERP-ack write-back, §0 Q10). Every Portal→ERP transaction
/// returns an ack; on success the ERP returns the entity's ERP code, pushed back here. <c>PortalRef</c> is the
/// deterministic outbox correlation id the portal echoed on the outbound post; the handler resolves it to
/// EXACTLY ONE outbox row of the stated <c>TransactionType</c>, writes <c>ErpCode</c> to the matching record
/// (Supplier→SupCode, Asn→ASNNo, plus Invoice/GoodsReceipt/Payment/Address/Contact/Bank/License/change-line)
/// and flips the outbox row to <see cref="OutboxStatus.Acked"/>.
///
/// <para><b>Risk R17 — correlation drift.</b> The match is on the deterministic key (review B2: now tenant+
/// supplier-qualified and unique on live rows per the composite UQ_OutboxMessage_tenant_deterministicKey), so a
/// wrong/duplicate ack cannot stamp the wrong record: a re-ack of an already-Acked row is an idempotent no-op;
/// a portalRef that resolves to NO row, to a row of a DIFFERENT transactionType, or to a record that no longer
/// exists logs an <c>IntegrationError</c> and writes NOTHING. Tenant-scoped via
/// <see cref="TenantInboundUpsertExecutor"/> (endpoint gate, idempotency, transactional
/// SyncLog/IntegrationError, endpoint-session telemetry) — the outbox row is keyed by (tenant, deterministic key).</para>
///
/// <para><b>Review S3 — company anti-spoof.</b> The ack endpoint now passes the key's <c>BoundCompanyIds</c> (as the
/// other three transactional endpoints do). The handler resolves each target record's company
/// (<c>TenantEntityId</c>) and REQUIRES it in the key's bound set before any write — a key bound only to company
/// 2000 cannot erp-code-write-back a company-9000 invoice within the same tenant.</para>
///
/// <para><b>Review S4 — tenant predicate on target writes.</b> Every target lookup in <c>StampErpCodeAsync</c>
/// additionally filters <c>x.TenantId == tenantId</c> and fails the row on a mismatch, rather than writing by
/// <c>Id</c> alone (defence-in-depth: no write onto an unguarded cross-tenant record even if a key were ever
/// mis-resolved).</para>
/// </summary>
public record UpsertErpAckCommand(PushErpAckRequest Body, IReadOnlySet<Guid> BoundCompanyIds, string? IdempotencyKey)
    : IRequest<UpsertResultDto>;

public class UpsertErpAckCommandValidator : AbstractValidator<UpsertErpAckCommand>
{
    public UpsertErpAckCommandValidator()
    {
        RuleFor(x => x.Body.Acks).NotEmpty().Must(a => a == null || a.Count <= 1000)
            .WithMessage("Between 1 and 1000 acks per batch.");
        RuleForEach(x => x.Body.Acks).ChildRules(a =>
        {
            a.RuleFor(r => r.TransactionType).NotEmpty().MaximumLength(50);
            a.RuleFor(r => r.PortalRef).NotEmpty().MaximumLength(128);
            // On success the ERP must return the code to write back.
            a.RuleFor(r => r.ErpCode).NotEmpty().When(r => r.Success)
                .WithMessage("erpCode is required on a successful ack (it is the value written back to the record).");
        });
    }
}

public class UpsertErpAckCommandHandler(TenantInboundUpsertExecutor exec) : IRequestHandler<UpsertErpAckCommand, UpsertResultDto>
{
    public Task<UpsertResultDto> Handle(UpsertErpAckCommand request, CancellationToken ct)
    {
        var recs = request.Body.Acks;
        var canonical = recs.Select(r => $"{r.TransactionType.Trim()}|{r.PortalRef.Trim()}|{r.Success}|{(r.ErpCode ?? "").Trim()}");
        var codes = recs.Select(r => r.PortalRef.Trim());

        return exec.ExecuteAsync(TransactionalInboundEntity.ErpAck, request.IdempotencyKey,
            recs.Count, canonical, codes, request.Body, Upsert, ct);

        async Task<IReadOnlyList<RowResult>> Upsert(IAppDbContext db, Guid tenantId, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var results = new List<RowResult>(recs.Count);

            foreach (var rec in recs)
            {
                var portalRef = rec.PortalRef.Trim();
                var txType = rec.TransactionType.Trim();

                // Resolve portalRef → outbox row within the tenant. Review B2/B3: the deterministic key is now
                // tenant+supplier-qualified and unique on live rows per the composite (TenantId, DeterministicKey)
                // UQ, so this (TenantId, DeterministicKey) lookup is genuinely exactly-one. IgnoreQueryFilters: the
                // service principal has no tenant context on the outbox (system infra); restrict by tenant explicitly.
                var row = await db.OutboxMessages.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(m => !m.IsDeleted && m.TenantId == tenantId && m.DeterministicKey == portalRef, token);

                if (row is null)
                {
                    results.Add(new RowResult(portalRef, RowOutcome.Failed, $"No outbox row for portalRef '{portalRef}'."));
                    continue;
                }

                // Anti-drift: the ack's transactionType MUST match the resolved row's — never stamp across types.
                if (!string.Equals(row.TransactionType, txType, StringComparison.Ordinal))
                {
                    results.Add(new RowResult(portalRef, RowOutcome.Failed,
                        $"transactionType mismatch: ack '{txType}' vs outbox '{row.TransactionType}' (risk R17 — no write)."));
                    continue;
                }

                // Idempotent re-ack: an already-Acked row is a no-op.
                if (row.Status == OutboxStatus.Acked)
                {
                    results.Add(new RowResult(portalRef, RowOutcome.Skipped, null));
                    continue;
                }

                if (!rec.Success)
                {
                    // A negative ack: record the failure, leave erpCode untouched, do NOT mark Acked (it stays
                    // retryable). The ERP-side message is preserved on the row for the operator.
                    row.LastError = string.IsNullOrWhiteSpace(rec.Message) ? "ERP ack reported failure." : rec.Message!.Trim();
                    row.UpdatedBy = "infor:inbound";
                    row.UpdatedOn = now;
                    results.Add(new RowResult(portalRef, RowOutcome.Failed,
                        $"ERP ack failure for {txType}: {row.LastError}"));
                    continue;
                }

                var erpCode = rec.ErpCode!.Trim();

                // Write the returned ERP code to the matching record. The mapping is by the outbox row's
                // (TransactionType, EntityId). A record that no longer exists ⇒ failure, no Acked flip. Review S3/S4:
                // the lookup is tenant-scoped and the target's company is required in the key's bound set.
                var stamped = await StampErpCodeAsync(db, tenantId, request.BoundCompanyIds, row.TransactionType, row.EntityId, erpCode, now, token);
                if (!stamped.ok)
                {
                    results.Add(new RowResult(portalRef, RowOutcome.Failed, stamped.error));
                    continue;
                }

                row.Status = OutboxStatus.Acked;
                row.AckedAt = now;
                row.LastError = null;
                row.UpdatedBy = "infor:inbound";
                row.UpdatedOn = now;
                results.Add(new RowResult(portalRef, RowOutcome.Updated, null));
            }
            return results;
        }
    }

    /// <summary>
    /// Stamp <paramref name="erpCode"/> onto the record the outbox row concerns. The transactionType selects the
    /// target table; EntityId selects the row. Supplier→SupCode and Asn→ASNNo are surfaced via the entity's
    /// ErpCode column (the denormalized ERP handle). Returns (false, error) when the target row is missing.
    ///
    /// <para>Review S4 — every lookup additionally filters <c>x.TenantId == tenantId</c>; a target in another tenant
    /// (or no longer present) fails the row, never a blind write-by-Id.</para>
    /// <para>Review S3 — the resolved target's company (<c>TenantEntityId</c>) must be in the key's
    /// <paramref name="boundCompanyIds"/>; a company the key is not bound to fails the row.</para>
    /// </summary>
    private static async Task<(bool ok, string? error)> StampErpCodeAsync(
        IAppDbContext db, Guid tenantId, IReadOnlySet<Guid> boundCompanyIds,
        string transactionType, Guid? entityId, string erpCode, DateTime now, CancellationToken ct)
    {
        if (entityId is null)
            return (false, $"Outbox row for '{transactionType}' has no entityId to stamp.");

        var id = entityId.Value;

        // Review S3 — the ack may only touch a record whose company is in the key's bound set. A company-scoped
        // record (TenantEntityId set) MUST be bound; a tenant-level record (TenantEntityId null — e.g. a
        // tenant-wide supplier) has no company to spoof and is governed by the tenant scope (S4) alone.
        static bool CompanyBound(IReadOnlySet<Guid> bound, Guid? companyId)
            => companyId is not Guid c || (bound is { Count: > 0 } && bound.Contains(c));

        switch (transactionType)
        {
            case OutboxTransactionType.SupplierSync:
            {
                var e = await db.Suppliers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
                if (e is null) return (false, $"Supplier {id} not found for ack in this tenant.");
                if (!CompanyBound(boundCompanyIds, e.TenantEntityId))
                    return (false, $"Supplier {id} company is not in the API key's bound set (review S3 — no write).");
                e.ErpCode = erpCode; e.UpdatedBy = "infor:inbound"; e.UpdatedOn = now;
                return (true, null);
            }
            case OutboxTransactionType.AsnPost:
            {
                var e = await db.Asns.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
                if (e is null) return (false, $"Asn {id} not found for ack in this tenant.");
                if (!CompanyBound(boundCompanyIds, e.TenantEntityId))
                    return (false, $"Asn {id} company is not in the API key's bound set (review S3 — no write).");
                e.ErpCode = erpCode; e.UpdatedBy = "infor:inbound"; e.UpdatedOn = now;
                return (true, null);
            }
            case OutboxTransactionType.InvoicePost:
            {
                var e = await db.Invoices.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
                if (e is null) return (false, $"Invoice {id} not found for ack in this tenant.");
                if (!CompanyBound(boundCompanyIds, e.TenantEntityId))
                    return (false, $"Invoice {id} company is not in the API key's bound set (review S3 — no write).");
                e.ErpCode = erpCode; e.UpdatedBy = "infor:inbound"; e.UpdatedOn = now;
                // S2 — the erp-ack for an InvoicePost is also a "post genuinely landed" signal; promote
                // initiated→posted if the dispatcher has not already done so (idempotent).
                e.ErpPostedAt ??= now;
                return (true, null);
            }
            case OutboxTransactionType.PoAcknowledge:
            case OutboxTransactionType.PoAccept:
            case OutboxTransactionType.PoReject:
            {
                // PO is ERP-owned (read-only in the portal) — there is no ErpCode column to write. The ack still
                // confirms the response post landed; flip the outbox to Acked (no record write). Review S3/S4: still
                // verify the PO is in this tenant AND its company is bound before accepting the ack.
                var po = await db.PurchaseOrders.IgnoreQueryFilters()
                    .Where(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted)
                    .Select(x => new { x.TenantEntityId })
                    .FirstOrDefaultAsync(ct);
                if (po is null) return (false, $"PurchaseOrder {id} not found for ack in this tenant.");
                if (!CompanyBound(boundCompanyIds, po.TenantEntityId))
                    return (false, $"PurchaseOrder {id} company is not in the API key's bound set (review S3 — no write).");
                return (true, null);
            }
            case OutboxTransactionType.SupplierChange:
            {
                // The change-request push acks per-line; the outbox EntityId is the change-request id. Review S3/S4:
                // resolve the request within the tenant + verify its company is bound, then stamp the erpRef on its
                // pending/pushed lines (the ERP returns one handle per pushed entity; Phase 1 maps the request-level
                // ack onto all its lines awaiting a handle).
                var req = await db.SupplierChangeRequests.IgnoreQueryFilters()
                    .Where(r => r.Id == id && r.TenantId == tenantId && !r.IsDeleted)
                    .Select(r => new { r.TenantEntityId })
                    .FirstOrDefaultAsync(ct);
                if (req is null) return (false, $"SupplierChangeRequest {id} not found for ack in this tenant.");
                if (!CompanyBound(boundCompanyIds, req.TenantEntityId))
                    return (false, $"SupplierChangeRequest {id} company is not in the API key's bound set (review S3 — no write).");

                var lines = await db.SupplierChangeRequestLines.IgnoreQueryFilters()
                    .Where(l => !l.IsDeleted && l.SupplierChangeRequestId == id && l.ErpRef == null)
                    .ToListAsync(ct);
                if (lines.Count == 0)
                    return (false, $"SupplierChangeRequest {id} has no lines awaiting an ERP ref.");
                foreach (var l in lines)
                {
                    l.ErpRef = erpCode;
                    l.PushStatus = LinePushStatus.Pushed;
                    l.PushedAt = now;
                    l.UpdatedBy = "infor:inbound";
                    l.UpdatedOn = now;
                }
                return (true, null);
            }
            default:
                return (false, $"No erp-ack stamp route for transactionType '{transactionType}'.");
        }
    }
}
