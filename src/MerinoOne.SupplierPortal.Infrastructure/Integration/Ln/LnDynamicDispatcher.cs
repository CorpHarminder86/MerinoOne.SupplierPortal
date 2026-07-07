using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Domain.Entities.Integration;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.2/§2.3) — the config-driven dispatch pipeline for one claimed outbox row:
/// input document → request mapping → canonical bytes → POST → response mapping → closed contract.
/// Invoked by <c>OutboxDispatcherWorker</c> ONLY when the row's endpoint config is <c>Dynamic</c>;
/// Legacy/absent routes stay on the compiled path, byte-identical to today.
///
/// <para><b>Mock parity:</b> when <c>Integration:Mode != Live</c> the request expression still runs and
/// the canonical payload lands in the SyncLog (byte-parity holds in Mock), but no HTTP fires and no
/// ErpCode returns — the row lands <c>Dispatched</c> awaiting erp-ack, exactly like the legacy Mock.</para>
///
/// <para><b>D-R9-20:</b> on a landed 2xx with a contract-valid response, <c>erpStatus</c> is written to
/// the owning entity's EXISTING ERP-owned status column — today only <c>PurchaseOrder.ErpStatus</c>
/// exists (also written by inbound PO replication; both writers carry ERP truth, last-writer-wins is
/// safe). Other entities have no target: the value stays in the SyncLog message. Never the portal
/// workflow status, never the outbox status.</para>
/// </summary>
public interface ILnDynamicDispatcher
{
    Task<LnDispatchOutcome> DispatchAsync(OutboxMessage row, LnEndpointRoute route, CancellationToken ct = default);
}

/// <summary>Dispatch outcome + the code-owned permanence class (D-R9-5) for the worker's failure stamping.</summary>
public sealed record LnDispatchOutcome(InforSyncResult Result, bool PermanentFailure);

/// <summary>Per-drain-cycle projection of one live <c>OutboundIntegrationConfig</c> row (the worker's routing map).</summary>
public sealed record LnEndpointRoute(
    Guid? TenantId,
    string TransactionType,
    OutboundDispatchMode Mode,
    string PortalEntity,
    string EndpointPath,
    string HttpVerb,
    string RequestMappingExpr,
    string ResponseMappingExpr);

public sealed class LnDynamicDispatcher : ILnDynamicDispatcher
{
    private readonly IAppDbContext _db;
    private readonly ILnInputDocumentBuilderRegistry _builders;
    private readonly ILnMappingService _mapping;
    private readonly ILnHttpTransport _transport;
    private readonly LnDefaultExpressions _defaults;
    private readonly IConfiguration _cfg;
    private readonly ILogger<LnDynamicDispatcher> _logger;

    public LnDynamicDispatcher(
        IAppDbContext db,
        ILnInputDocumentBuilderRegistry builders,
        ILnMappingService mapping,
        ILnHttpTransport transport,
        LnDefaultExpressions defaults,
        IConfiguration cfg,
        ILogger<LnDynamicDispatcher> logger)
    {
        _db = db;
        _builders = builders;
        _mapping = mapping;
        _transport = transport;
        _defaults = defaults;
        _cfg = cfg;
        _logger = logger;
    }

    public async Task<LnDispatchOutcome> DispatchAsync(OutboxMessage row, LnEndpointRoute route, CancellationToken ct = default)
    {
        // --- 1. Input document (portalEntity ALWAYS from the config route, never OutboxMessage.EntityName). ------
        var builder = _builders.TryGet(route.PortalEntity);
        if (builder is null)
            return Permanent(row, $"No input-document builder for portalEntity '{route.PortalEntity}' — fix the endpoint config.");
        if (row.EntityId is not Guid entityId)
            return Permanent(row, $"Outbox row {row.Id} carries no EntityId — cannot build the input document.");

        var inputJson = await builder.BuildJsonAsync(_db, entityId, row.TransactionType, row.PayloadJson, ct);
        if (inputJson is null)
            return Retriable(row, $"[{route.PortalEntity}] entity {entityId} not found.", null);

        // --- 2. Request mapping (an eval failure is a CONFIG bug — permanent, no LN call). -----------------------
        var request = _mapping.Evaluate(route.RequestMappingExpr, inputJson);
        if (!request.Ok || request.OutputJson is null)
            return Permanent(row, $"Request mapping failed for {row.TransactionType}: {request.Error ?? "expression produced no output"}");

        // --- 3. Canonical bytes: the SAME form the parity harness certifies — this IS the wire body. -------------
        string canonicalBody;
        try { canonicalBody = LnJson.CanonicalWrite(request.OutputJson); }
        catch (Exception ex)
        {
            return Permanent(row, $"Request mapping for {row.TransactionType} produced non-JSON output: {ex.Message}");
        }

        // --- 4. Mock short-circuit: expression evaluated, canonical payload logged, no HTTP, no ErpCode. ----------
        var isLive = string.Equals(_cfg["Integration:Mode"], "Live", StringComparison.OrdinalIgnoreCase);
        if (!isLive)
        {
            return new LnDispatchOutcome(new InforSyncResult(
                true, row.DeterministicKey, $"[mock] dynamic {row.TransactionType} accepted.", canonicalBody, ErpCode: null), false);
        }

        // --- 5. Live POST. ---------------------------------------------------------------------------------------
        if (row.TenantId is not Guid tenantId)
            return Permanent(row, "Outbox row carries no tenant — cannot resolve the LN connection.");

        var outcome = await _transport.SendAsync(tenantId, route.HttpVerb, route.EndpointPath, canonicalBody, row.DeterministicKey, ct);

        if (outcome.StatusCode is null)
            return Retriable(row, $"[{route.PortalEntity}] {outcome.Error ?? "transport failure"}", canonicalBody);

        if (!outcome.IsHttpSuccess)
        {
            // Non-2xx: enrich the error TEXT via the shared default expression (D-R9-5 — text only);
            // permanence comes from the code-owned classifier, which no mapping can influence.
            var detail = ExtractErrorText(outcome.ResponseBody) ?? Truncate(outcome.ResponseBody ?? string.Empty, 300);
            var message = $"[{route.PortalEntity}] Infor rejected the request (HTTP {outcome.StatusCode}): {detail}";
            var permanent = LnRetriabilityClassifier.IsPermanent(outcome.StatusCode);
            return new LnDispatchOutcome(new InforSyncResult(false, row.DeterministicKey, message, canonicalBody), permanent);
        }

        // --- 6. Response mapping → closed contract (D-R9-4). ------------------------------------------------------
        var mapped = _mapping.Evaluate(route.ResponseMappingExpr, string.IsNullOrWhiteSpace(outcome.ResponseBody) ? "{}" : outcome.ResponseBody!);
        var (ack, contractErrors) = mapped.Ok
            ? LnClosedContract.Parse(mapped.OutputJson)
            : (null, new[] { mapped.Error ?? "response mapping evaluation failed" } as IReadOnlyList<string>);

        if (ack is null)
        {
            // Nastiest state: the POST LANDED but the mapped output violates the contract. Retriable (LN
            // dedupes the replayed idempotency key) with an explicit operator warning — same alert-only
            // posture as the stale-Dispatched sweep.
            var message = $"[{route.PortalEntity}] POST landed (HTTP {outcome.StatusCode}) but the response mapping "
                        + $"produced non-contract output — VERIFY IN LN BEFORE RE-ARM. {string.Join(" ", contractErrors)}";
            _logger.LogError("LN dynamic response-contract failure for {Tx} {RowId}: {Message}", row.TransactionType, row.Id, message);
            return new LnDispatchOutcome(new InforSyncResult(false, row.DeterministicKey, message, canonicalBody), false);
        }

        // --- 7. D-R9-20 — erpStatus → the entity's existing ERP-owned status column (PO responses only today). ----
        if (row.TransactionType is OutboxTransactionType.PoAcknowledge or OutboxTransactionType.PoAccept or OutboxTransactionType.PoReject)
        {
            await _db.PurchaseOrders
                .IgnoreQueryFilters()
                .Where(p => p.Id == entityId && !p.IsDeleted)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.ErpStatus, Truncate(ack.ErpStatus, 50))
                    .SetProperty(p => p.UpdatedBy, "outbox-dispatcher")
                    .SetProperty(p => p.UpdatedOn, DateTime.UtcNow), ct);
        }

        // ErpCode = the extracted erpKey → the worker's existing sync-ack seam flips the row straight to Acked.
        var okMessage = string.IsNullOrWhiteSpace(ack.Message)
            ? $"[{route.PortalEntity}] {ack.ErpStatus} (HTTP {outcome.StatusCode})."
            : $"[{route.PortalEntity}] {ack.ErpStatus} (HTTP {outcome.StatusCode}): {ack.Message}";
        return new LnDispatchOutcome(new InforSyncResult(true, row.DeterministicKey, okMessage, canonicalBody, ErpCode: ack.ErpKey), false);
    }

    private string? ExtractErrorText(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        var result = _mapping.Evaluate(_defaults.ErrorMessageExpression, responseBody);
        if (!result.Ok || result.OutputJson is null) return null;
        // The expression yields a JSON string — unwrap the quotes for the human-facing message.
        var text = result.OutputJson.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"'
            ? System.Text.Json.JsonSerializer.Deserialize<string>(text)
            : text;
    }

    private static LnDispatchOutcome Permanent(OutboxMessage row, string message)
        => new(new InforSyncResult(false, row.DeterministicKey, message), true);

    private static LnDispatchOutcome Retriable(OutboxMessage row, string message, string? payloadJson)
        => new(new InforSyncResult(false, row.DeterministicKey, message, payloadJson), false);

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
