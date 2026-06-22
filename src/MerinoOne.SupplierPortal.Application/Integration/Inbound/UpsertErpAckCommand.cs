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
/// <para><b>Risk R17 — correlation drift.</b> The match is on the deterministic key (unique on live rows), so a
/// wrong/duplicate ack cannot stamp the wrong record: a re-ack of an already-Acked row is an idempotent no-op;
/// a portalRef that resolves to NO row, to a row of a DIFFERENT transactionType, or to a record that no longer
/// exists logs an <c>IntegrationError</c> and writes NOTHING. Tenant-scoped via
/// <see cref="TenantInboundUpsertExecutor"/> (endpoint gate, idempotency, transactional
/// SyncLog/IntegrationError, endpoint-session telemetry) — the outbox row is keyed by tenant + deterministic key.</para>
/// </summary>
public record UpsertErpAckCommand(PushErpAckRequest Body, string? IdempotencyKey) : IRequest<UpsertResultDto>;

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

                // Resolve portalRef → outbox row within the tenant. The deterministic key is unique on live rows,
                // so this is exactly-one. IgnoreQueryFilters: the service principal has no tenant context on the
                // outbox (system infra); restrict by tenant explicitly.
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
                // (TransactionType, EntityId). A record that no longer exists ⇒ failure, no Acked flip.
                var stamped = await StampErpCodeAsync(db, row.TransactionType, row.EntityId, erpCode, now, token);
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
    /// </summary>
    private static async Task<(bool ok, string? error)> StampErpCodeAsync(
        IAppDbContext db, string transactionType, Guid? entityId, string erpCode, DateTime now, CancellationToken ct)
    {
        if (entityId is null)
            return (false, $"Outbox row for '{transactionType}' has no entityId to stamp.");

        var id = entityId.Value;

        switch (transactionType)
        {
            case OutboxTransactionType.SupplierSync:
            {
                var e = await db.Suppliers.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
                if (e is null) return (false, $"Supplier {id} not found for ack.");
                e.ErpCode = erpCode; e.UpdatedBy = "infor:inbound"; e.UpdatedOn = now;
                return (true, null);
            }
            case OutboxTransactionType.AsnPost:
            {
                var e = await db.Asns.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
                if (e is null) return (false, $"Asn {id} not found for ack.");
                e.ErpCode = erpCode; e.UpdatedBy = "infor:inbound"; e.UpdatedOn = now;
                return (true, null);
            }
            case OutboxTransactionType.InvoicePost:
            {
                var e = await db.Invoices.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
                if (e is null) return (false, $"Invoice {id} not found for ack.");
                e.ErpCode = erpCode; e.UpdatedBy = "infor:inbound"; e.UpdatedOn = now;
                return (true, null);
            }
            case OutboxTransactionType.PoAcknowledge:
            case OutboxTransactionType.PoAccept:
            case OutboxTransactionType.PoReject:
            {
                // PO is ERP-owned (read-only in the portal) — there is no ErpCode column to write. The ack still
                // confirms the response post landed; flip the outbox to Acked (no record write needed).
                return (true, null);
            }
            case OutboxTransactionType.SupplierChange:
            {
                // The change-request push acks per-line; the outbox EntityId is the change-request id. Stamp the
                // erpRef on its pending/pushed lines (the ERP returns one handle per pushed entity; Phase 1 maps
                // the request-level ack onto all its lines awaiting a handle).
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
