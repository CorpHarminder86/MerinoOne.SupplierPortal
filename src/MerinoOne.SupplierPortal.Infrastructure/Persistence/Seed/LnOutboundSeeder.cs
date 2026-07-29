using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;
using MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;

/// <summary>
/// R9 (TSD R9 §2.1) — seeds one <c>OutboundIntegrationConfig</c> row per tenant per transaction type (all 8) from
/// the repo-embedded expression catalogue. Every row seeds <c>DispatchMode=Legacy</c> — creating config
/// rows changes NOTHING at dispatch until an admin attests + flips to Dynamic (D-R9-2/17/21).
/// Idempotent with the IdmOutboundSeeder per-slot hash-gate: on re-seed an expression is overwritten ONLY
/// when the stored text is still untouched since the last seed (current hash == stored seed hash) and the
/// repo default changed; hand-edited rows are left alone (that difference IS the drift flag).
/// </summary>
public static class LnOutboundSeeder
{
    private const string Actor = "seed:r9-ln";

    /// <summary>transactionType → starter endpoint path (the same constants Live uses — one source until the tenant export).</summary>
    private static readonly IReadOnlyDictionary<string, string> EndpointPathByType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OutboxTransactionType.InvoicePost] = LiveInforIntegrationService.EndpointPaths.Invoice,
            [OutboxTransactionType.AsnPost] = LiveInforIntegrationService.EndpointPaths.Asn,
            [OutboxTransactionType.SupplierChange] = LiveInforIntegrationService.EndpointPaths.SupplierChange,
            [OutboxTransactionType.SupplierSync] = LiveInforIntegrationService.EndpointPaths.Supplier,
            // R12 — the three PO-response transactions share ONE real endpoint; POStatus in the body is the
            // only thing that differs. Three rows, one path: a future split stays a per-row edit.
            [OutboxTransactionType.PoAccept] = LiveInforIntegrationService.EndpointPaths.PoUpdate,
            [OutboxTransactionType.PoReject] = LiveInforIntegrationService.EndpointPaths.PoUpdate,
            [OutboxTransactionType.PoNegotiationApprove] = LiveInforIntegrationService.EndpointPaths.PoUpdate,
        };

    /// <summary>transactionType → seed candidate filter (TSD §2.5a, execution-reconfirmed names; PO pair shares StatusIn).</summary>
    private static readonly IReadOnlyDictionary<string, (string Name, string? ParamsJson)> CandidateFilterByType =
        new Dictionary<string, (string, string?)>(StringComparer.Ordinal)
        {
            [OutboxTransactionType.InvoicePost] = ("InvoiceSubmittedUnposted", null),
            [OutboxTransactionType.AsnPost] = ("AsnSubmitted", null),
            [OutboxTransactionType.PoAccept] = ("StatusIn", "{\"statuses\":[\"Accepted\"]}"),
            [OutboxTransactionType.PoReject] = ("StatusIn", "{\"statuses\":[\"Rejected\"]}"),
            [OutboxTransactionType.SupplierChange] = ("SupplierChangeApproved", null),
            [OutboxTransactionType.SupplierSync] = ("SupplierRegistrationApprovedNoErpCode", null),
            [OutboxTransactionType.PoNegotiationApprove] = ("PoNegotiationApproved", null),
        };

    /// <summary>
    /// R12 (D16) — seed eligibility gates for the PO_Update transactions, evaluated over the input document.
    ///
    /// <para><b>Monotonic facts, never current-state equality.</b> A gate re-check runs at dispatch, and a row
    /// can be re-armed days after it was enqueued — during the R12 cutover window, routinely. <c>poStatus =
    /// "Accepted"</c> would look right and then permanently <c>Skip</c> a re-armed row whose PO had since moved
    /// to PartiallyDelivered or FullyShipped, so LN would never learn the PO was accepted at all. "It once
    /// reached this state" cannot go stale that way. The candidate filters keep doing the set-narrowing.</para>
    ///
    /// <para><b>Reject is the exception</b>: <c>PoStatus.Rejected</c> is terminal, so equality is already
    /// monotonic there and reads more plainly than an existence check.</para>
    ///
    /// <para>!! Never write <c>poStatus = "Negotiated"</c>. That value exists only on the wire — approving a
    /// negotiation sets the PO to <c>Approved</c>. Gates are strict-true/fail-closed, so the literal would skip
    /// every negotiation and look like nothing was wrong.</para>
    /// </summary>
    // internal, not private: LnPoGateExpressionTests evaluates THESE strings. A test that re-declared its own
    // copy would pass while the shipped gate was broken.
    internal static readonly IReadOnlyDictionary<string, string> EligibilityGateByType =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OutboxTransactionType.PoAccept] = "$exists(acceptedAt)",
            [OutboxTransactionType.PoReject] = "poStatus = \"Rejected\"",
            [OutboxTransactionType.PoNegotiationApprove] = "negotiationStatus = \"Approved\"",
        };

    /// <summary>R12 — the transactions whose responseSampleJson is the PO_Update envelope, not the OData one.</summary>
    private static bool IsPoUpdate(string transactionType)
        => transactionType is OutboxTransactionType.PoAccept
            or OutboxTransactionType.PoReject
            or OutboxTransactionType.PoNegotiationApprove;

    public static async Task SeedAsync(AppDbContext ctx, CancellationToken ct = default)
    {
        var defaults = new LnDefaultExpressions();
        var tenantIds = await ctx.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(ct);

        foreach (var tenantId in tenantIds)
        {
            // R10 — every tenant gets exactly one default connection point ("Default — Infor ION"): base URL +
            // OAuth resolve from the tenant's Infor connection settings, so the row is pure identity. Configs
            // with a NULL connectionPointId dispatch through it — the pre-R10 behavior, now named.
            var hasDefault = await ctx.ConnectionPoints.IgnoreQueryFilters()
                .AnyAsync(p => p.TenantId == tenantId && p.IsDefault && !p.IsDeleted, ct);
            if (!hasDefault)
            {
                ctx.ConnectionPoints.Add(new ConnectionPoint
                {
                    TenantId = tenantId,
                    Name = "Default — Infor ION",
                    SystemType = ConnectionSystemTypes.InforIon,
                    IsDefault = true,
                    Notes = "Seeded default. Base URL and OAuth resolve from Settings → Infor CloudSuite.",
                    CreatedBy = Actor,
                });
            }

            // R12 (D14) — PoAcknowledge no longer posts to LN, so retire its config row. Soft-delete, not hard:
            // the outbox monitor and sync-log history still join to it for rows that predate the change.
            var retiredAck = await ctx.OutboundIntegrationConfigs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Kind == OutboundIntegrationKind.Transaction
                                          && c.TransactionType == OutboxTransactionType.PoAcknowledge && !c.IsDeleted, ct);
            if (retiredAck is not null)
            {
                retiredAck.IsDeleted = true;
                retiredAck.UpdatedBy = Actor;
                retiredAck.UpdatedOn = DateTime.UtcNow;
            }

            foreach (var entry in defaults.All)
            {
                var existing = await ctx.OutboundIntegrationConfigs.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Kind == OutboundIntegrationKind.Transaction
                                              && c.TransactionType == entry.TransactionType && !c.IsDeleted, ct);

                if (existing is null)
                {
                    var filter = CandidateFilterByType[entry.TransactionType];
                    ctx.OutboundIntegrationConfigs.Add(new OutboundIntegrationConfig
                    {
                        TenantId = tenantId,
                        Kind = OutboundIntegrationKind.Transaction,
                        TransactionType = entry.TransactionType,
                        PortalEntity = entry.PortalEntity,
                        EndpointPath = EndpointPathByType[entry.TransactionType],
                        HttpVerb = "POST",
                        DispatchMode = OutboundDispatchMode.Legacy,
                        RequestMappingExpr = entry.RequestExpr,
                        RequestMappingSeedHash = entry.RequestHash,
                        ResponseMappingExpr = entry.ResponseExpr,
                        ResponseMappingSeedHash = entry.ResponseHash,
                        AckMappingExpr = entry.AckExpr,
                        AckMappingSeedHash = entry.AckHash,
                        EligibilityGateExpr = EligibilityGateByType.GetValueOrDefault(entry.TransactionType),
                        CandidateFilterName = filter.Name,
                        CandidateFilterParams = filter.ParamsJson,
                        GateVersion = 1,
                        ResponseSampleJson = IsPoUpdate(entry.TransactionType)
                            ? defaults.PoUpdateResponseSample
                            : defaults.ODataCreatedEntitySample,
                        AckSampleJson = defaults.ErpAckBodySample,
                        CreatedBy = Actor,
                    });
                    continue;
                }

                // ── R12 — corrections to ALREADY-SEEDED rows ──────────────────────────────────────────────
                // Before R12 this method only ever touched expression slots on an existing row, so endpointPath
                // and responseSampleJson were effectively insert-only: a tenant seeded before R12 would keep
                // pointing at "…/Acceptances" forever and 404 on every PO push. Correct them here, under the
                // same "untouched since seed" discipline the expression hash-gate uses — a hand-edited value is
                // left alone, because that divergence IS the drift signal.
                if (IsPoUpdate(entry.TransactionType))
                {
                    if (LiveInforIntegrationService.EndpointPaths.RetiredPoStarterPaths
                            .Contains(existing.EndpointPath, StringComparer.Ordinal))
                    {
                        existing.EndpointPath = EndpointPathByType[entry.TransactionType];
                        existing.UpdatedBy = Actor;
                        existing.UpdatedOn = DateTime.UtcNow;
                    }

                    // The generic OData sample cannot validate a PO_Update response mapping — swap it for the
                    // real envelope, but only while it is still the seeded generic one.
                    if (string.IsNullOrWhiteSpace(existing.ResponseSampleJson)
                        || string.Equals(existing.ResponseSampleJson, defaults.ODataCreatedEntitySample, StringComparison.Ordinal))
                    {
                        existing.ResponseSampleJson = defaults.PoUpdateResponseSample;
                        existing.UpdatedBy = Actor;
                        existing.UpdatedOn = DateTime.UtcNow;
                    }
                }

                // D16 gates land ONLY in a blank slot. There is no eligibilityGateSeedHash column, so the
                // hash-gate trick is unavailable and "never overwrite an authored gate" is the safe rule — an
                // admin's gate outranks a repo default. Writing a gate bumps gateVersion, which is what makes
                // D17 able to tell rows enqueued before the gate from rows enqueued after it.
                if (string.IsNullOrWhiteSpace(existing.EligibilityGateExpr)
                    && EligibilityGateByType.TryGetValue(entry.TransactionType, out var gate))
                {
                    existing.EligibilityGateExpr = gate;
                    existing.GateVersion += 1;
                    existing.UpdatedBy = Actor;
                    existing.UpdatedOn = DateTime.UtcNow;
                }

                // Per-slot hash-gate: overwrite ONLY untouched-since-seed slots whose repo default changed.
                var touched = false;
                if (Application.Common.Integration.ExpressionHash.Compute(existing.RequestMappingExpr) == existing.RequestMappingSeedHash
                    && existing.RequestMappingSeedHash != entry.RequestHash)
                {
                    existing.RequestMappingExpr = entry.RequestExpr;
                    existing.RequestMappingSeedHash = entry.RequestHash;
                    touched = true;
                }
                if (existing.ResponseMappingExpr is not null
                    && Application.Common.Integration.ExpressionHash.Compute(existing.ResponseMappingExpr) == existing.ResponseMappingSeedHash
                    && existing.ResponseMappingSeedHash != entry.ResponseHash)
                {
                    existing.ResponseMappingExpr = entry.ResponseExpr;
                    existing.ResponseMappingSeedHash = entry.ResponseHash;
                    touched = true;
                }
                if (existing.AckMappingExpr is not null
                    && Application.Common.Integration.ExpressionHash.Compute(existing.AckMappingExpr) == existing.AckMappingSeedHash
                    && existing.AckMappingSeedHash != entry.AckHash)
                {
                    existing.AckMappingExpr = entry.AckExpr;
                    existing.AckMappingSeedHash = entry.AckHash;
                    touched = true;
                }
                if (touched)
                {
                    existing.UpdatedBy = Actor;
                    existing.UpdatedOn = DateTime.UtcNow;
                }
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
