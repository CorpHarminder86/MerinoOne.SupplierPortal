using System.Linq.Expressions;
using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.CandidateFilters;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 — <see cref="ILnGateScanner"/>. Per portalEntity: apply the registry candidate filter (indexed,
/// parameterized SQL WHERE — nothing admin-authored reaches the DB, D-R9-15), project EXACTLY the key
/// material the enqueue site uses, derive the deterministic key, attach the live outbox status per key,
/// then evaluate the gate per candidate through <see cref="ILnEligibilityService"/> (committed state —
/// authoritative for sweep/backfill). The §2.5a over-inclusion note holds here: filters may include
/// already-posted rows; the attached row status is what keeps that harmless.
/// </summary>
public sealed class LnGateScanner : ILnGateScanner
{
    private readonly IAppDbContext _db;
    private readonly ICandidateFilterRegistry _filters;
    private readonly ILnEligibilityService _eligibility;

    public LnGateScanner(IAppDbContext db, ICandidateFilterRegistry filters, ILnEligibilityService eligibility)
    {
        _db = db;
        _filters = filters;
        _eligibility = eligibility;
    }

    private sealed record Candidate(Guid EntityId, Guid TenantId, string EntityName, string DeterministicKey);

    public async Task<IReadOnlyList<LnScanVerdict>> ScanAsync(OutboundIntegrationConfig config, int maxCandidates, CancellationToken ct = default)
    {
        if (config.TenantId is not { } tenantId)
            throw new InvalidOperationException("Scanner requires a tenant-scoped config row.");
        if (string.IsNullOrWhiteSpace(config.CandidateFilterName))
            throw new InvalidOperationException($"Config {config.TransactionType} has no candidate filter — the sweep pre-filter is not optional (D-R9-8).");

        var filter = _filters.Resolve(config.PortalEntity, config.CandidateFilterName, config.CandidateFilterParams);
        var candidates = await CandidatesAsync(config, tenantId, filter, maxCandidates, ct);
        if (candidates.Count == 0) return Array.Empty<LnScanVerdict>();

        // Live outbox rows on the candidate keys (ALL statuses — the UQ spans them; Skipped/Failed are re-armable).
        var keys = candidates.Select(c => c.DeterministicKey).ToList();
        var rowsByKey = await _db.OutboxMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && keys.Contains(m.DeterministicKey) && !m.IsDeleted)
            .Select(m => new { m.DeterministicKey, m.Status, m.Id })
            .ToDictionaryAsync(m => m.DeterministicKey, m => (m.Status.ToString(), m.Id), ct);

        var verdicts = new List<LnScanVerdict>(candidates.Count);
        foreach (var c in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var gate = await _eligibility.EvaluateAsync(tenantId, config.TransactionType, c.EntityId, null, ct);
            var existing = rowsByKey.TryGetValue(c.DeterministicKey, out var row) ? row : default((string, Guid)?);
            verdicts.Add(new LnScanVerdict(
                c.EntityId, c.TenantId, c.EntityName, c.DeterministicKey,
                existing?.Item1, existing?.Item2,
                // A blank gate on a gated-mode config means every filtered candidate is eligible.
                Eligible: !gate.HasGate || gate.Eligible,
                gate.Reason,
                gate.GateVersion ?? config.GateVersion));
        }
        return verdicts;
    }

    /// <summary>
    /// Key material per transaction type — VERBATIM from each enqueue site (golden-pinned by
    /// LnOutboxKeyDerivationTests; drift here means the sweep double-enqueues or never reconciles).
    /// </summary>
    private async Task<List<Candidate>> CandidatesAsync(
        OutboundIntegrationConfig config, Guid tenantId, LambdaExpression filter, int take, CancellationToken ct)
    {
        switch (config.PortalEntity)
        {
            case LnPortalEntity.Invoice:
            {
                var rows = await _db.Invoices.IgnoreQueryFilters()
                    .Where((Expression<Func<Domain.Entities.Proc.Invoice, bool>>)filter)
                    .Where(i => i.TenantId == tenantId)
                    .OrderBy(i => i.Seq).Take(take)
                    .Select(i => new { i.Id, i.SupplierId, i.InvoiceNumber })
                    .ToListAsync(ct);
                return rows.Select(i => new Candidate(i.Id, tenantId, OutboxEntity.Invoice,
                    OutboxKey.For(OutboxEntity.Invoice, tenantId, $"{i.SupplierId:N}|{i.InvoiceNumber}", "post"))).ToList();
            }
            case LnPortalEntity.Asn:
            {
                var rows = await _db.Asns.IgnoreQueryFilters()
                    .Where((Expression<Func<Domain.Entities.Proc.Asn, bool>>)filter)
                    .Where(a => a.TenantId == tenantId)
                    .OrderBy(a => a.Seq).Take(take)
                    .Select(a => new { a.Id, a.AsnNumber })
                    .ToListAsync(ct);
                return rows.Select(a => new Candidate(a.Id, tenantId, OutboxEntity.Asn,
                    OutboxKey.For(OutboxEntity.Asn, tenantId, a.AsnNumber, "submit"))).ToList();
            }
            case LnPortalEntity.PurchaseOrder:
            {
                var op = config.TransactionType switch
                {
                    OutboxTransactionType.PoAcknowledge => "acknowledge",
                    OutboxTransactionType.PoAccept => "accept",
                    OutboxTransactionType.PoReject => "reject",
                    _ => throw new InvalidOperationException($"PurchaseOrder scan does not serve '{config.TransactionType}'."),
                };
                var rows = await _db.PurchaseOrders.IgnoreQueryFilters()
                    .Where((Expression<Func<Domain.Entities.Proc.PurchaseOrder, bool>>)filter)
                    .Where(p => p.TenantId == tenantId)
                    .OrderBy(p => p.Seq).Take(take)
                    .Select(p => new { p.Id, p.PoNumber })
                    .ToListAsync(ct);
                return rows.Select(p => new Candidate(p.Id, tenantId, OutboxEntity.PurchaseOrder,
                    OutboxKey.For(OutboxEntity.PurchaseOrder, tenantId, p.PoNumber, op))).ToList();
            }
            case LnPortalEntity.Supplier:
            {
                // SupplierSync has NO code enqueue site — the sweep IS its trigger (TSD V2.2 changelog #2).
                // Proposed key material documented there: businessKey = {supplierId:N}, op = "sync".
                var rows = await _db.Suppliers.IgnoreQueryFilters()
                    .Where((Expression<Func<Domain.Entities.Supplier.Supplier, bool>>)filter)
                    .Where(s => s.TenantId == tenantId)
                    .OrderBy(s => s.Seq).Take(take)
                    .Select(s => new { s.Id })
                    .ToListAsync(ct);
                return rows.Select(s => new Candidate(s.Id, tenantId, OutboxEntity.Supplier,
                    OutboxKey.For(OutboxEntity.Supplier, tenantId, $"{s.Id:N}", "sync"))).ToList();
            }
            case LnPortalEntity.SupplierChange:
            {
                var rows = await _db.SupplierChangeRequests.IgnoreQueryFilters()
                    .Where((Expression<Func<Domain.Entities.Supplier.SupplierChangeRequest, bool>>)filter)
                    .Where(c => c.TenantId == tenantId)
                    .OrderBy(c => c.Seq).Take(take)
                    .Select(c => new { c.Id, c.SupplierId })
                    .ToListAsync(ct);
                return rows.Select(c => new Candidate(c.Id, tenantId, OutboxEntity.SupplierChange,
                    OutboxKey.For(OutboxEntity.SupplierChange, tenantId, $"{c.SupplierId:N}|{c.Id:N}", "push"))).ToList();
            }
            case LnPortalEntity.PoNegotiation:
            {
                // PoNegotiationApprove rows carry EntityName=PurchaseOrder with EntityId = the negotiation id.
                var rows = await _db.PurchaseOrderNegotiations.IgnoreQueryFilters()
                    .Where((Expression<Func<Domain.Entities.Proc.PurchaseOrderNegotiation, bool>>)filter)
                    .Where(n => n.TenantId == tenantId)
                    .OrderBy(n => n.Seq).Take(take)
                    .Select(n => new { n.Id, n.PoNumber })
                    .ToListAsync(ct);
                return rows.Select(n => new Candidate(n.Id, tenantId, OutboxEntity.PurchaseOrder,
                    OutboxKey.For(OutboxEntity.PurchaseOrder, tenantId, $"{n.PoNumber}|{n.Id:N}", "negotiation-approve"))).ToList();
            }
            default:
                throw new InvalidOperationException($"Unknown portalEntity '{config.PortalEntity}' for scanning.");
        }
    }
}
