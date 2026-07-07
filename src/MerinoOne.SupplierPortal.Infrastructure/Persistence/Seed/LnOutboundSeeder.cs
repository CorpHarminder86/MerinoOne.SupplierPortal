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
            [OutboxTransactionType.PoAcknowledge] = LiveInforIntegrationService.EndpointPaths.PurchaseOrderAck,
            [OutboxTransactionType.PoAccept] = LiveInforIntegrationService.EndpointPaths.PurchaseOrderAccept,
            [OutboxTransactionType.PoReject] = LiveInforIntegrationService.EndpointPaths.PurchaseOrderReject,
            [OutboxTransactionType.SupplierChange] = LiveInforIntegrationService.EndpointPaths.SupplierChange,
            [OutboxTransactionType.SupplierSync] = LiveInforIntegrationService.EndpointPaths.Supplier,
            [OutboxTransactionType.PoNegotiationApprove] = LiveInforIntegrationService.EndpointPaths.PoNegotiation,
        };

    /// <summary>transactionType → seed candidate filter (TSD §2.5a, execution-reconfirmed names; PO trio shares StatusIn).</summary>
    private static readonly IReadOnlyDictionary<string, (string Name, string? ParamsJson)> CandidateFilterByType =
        new Dictionary<string, (string, string?)>(StringComparer.Ordinal)
        {
            [OutboxTransactionType.InvoicePost] = ("InvoiceSubmittedUnposted", null),
            [OutboxTransactionType.AsnPost] = ("AsnSubmitted", null),
            [OutboxTransactionType.PoAcknowledge] = ("StatusIn", "{\"statuses\":[\"Acknowledged\"]}"),
            [OutboxTransactionType.PoAccept] = ("StatusIn", "{\"statuses\":[\"Accepted\"]}"),
            [OutboxTransactionType.PoReject] = ("StatusIn", "{\"statuses\":[\"Rejected\"]}"),
            [OutboxTransactionType.SupplierChange] = ("SupplierChangeApproved", null),
            [OutboxTransactionType.SupplierSync] = ("SupplierRegistrationApprovedNoErpCode", null),
            [OutboxTransactionType.PoNegotiationApprove] = ("PoNegotiationApproved", null),
        };

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
                        CandidateFilterName = filter.Name,
                        CandidateFilterParams = filter.ParamsJson,
                        GateVersion = 1,
                        ResponseSampleJson = defaults.ODataCreatedEntitySample,
                        AckSampleJson = defaults.ErpAckBodySample,
                        CreatedBy = Actor,
                    });
                    continue;
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
