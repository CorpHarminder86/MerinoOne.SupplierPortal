using MerinoOne.SupplierPortal.Application.Common.Integration;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Ln;
using MerinoOne.SupplierPortal.Contracts.Integration;
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
    string ResponseMappingExpr,
    /// <summary>R12 (D17) — the config's current gateVersion, matched against the row's to decide whether the
    /// dispatch-time gate re-check applies to THIS row. See <c>OutboxDispatcherWorker.DispatchOneAsync</c>.</summary>
    int GateVersion = 0);

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

        // --- 1a. R12 (D9) — company guard for the PO_Update transactions, code-owned and BEFORE any mapping. ---
        // LN's PO_Update routes on the body's CompanyCode, not the X-Infor-LnCompany header (D21), so unlike the
        // OData endpoints there is no header fallback that could rescue a PO with no TenantEntityId. R11.3
        // established that a wrong company is a wrong-company WRITE in LN; a missing one must therefore stop the
        // dispatch outright rather than post an order without a company and let LN decide where it lands.
        // Deliberately not left to the expression: a mapping is admin-editable, and this is a safety rule.
        if (IsPoUpdateTransaction(row.TransactionType) && !HasCompanyCode(inputJson))
            return Permanent(row,
                $"[{route.PortalEntity}] {row.TransactionType} has no company: the {(route.PortalEntity == LnPortalEntity.PoNegotiation ? "negotiation's purchase order" : "purchase order")} "
                + "carries no tenantEntityId, so CompanyCode cannot be set. Nothing was sent — fix the PO's company and re-arm.");

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

        // R11.3 — per-document X-Infor-LnCompany: LN routes on the header, so it must be the DOCUMENT's
        // company, not the tenant's first-listed one. Null (tenant-level doc) falls back inside the transport.
        var documentCompany = await Infor.LnDocumentCompanyResolver.ResolveAsync(_db, route.PortalEntity, entityId, ct);

        var outcome = await _transport.SendAsync(tenantId, route.HttpVerb, route.EndpointPath, canonicalBody, row.DeterministicKey, documentCompany, ct);

        if (outcome.StatusCode is null)
            return Retriable(row, $"[{route.PortalEntity}] {outcome.Error ?? "transport failure"}", canonicalBody);

        if (!outcome.IsHttpSuccess)
        {
            // Non-2xx: enrich the error TEXT via the shared default expression (D-R9-5 — text only);
            // permanence comes from the code-owned classifier, which no mapping can influence.
            // 1800 (not 300): the SOAP <detail> block names the offending attribute/line and must survive
            // into OutboxMessage.lastError (nvarchar(2000)) — the 2026-08-06 Quantity fault was diagnosed
            // blind because the block was truncated away.
            var detail = ExtractErrorText(outcome.ResponseBody) ?? Truncate(outcome.ResponseBody ?? string.Empty, 1800);
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

        // --- 6a. R12 (D12) — LN answers HTTP 200 with a LOGICAL verdict in Header.Status. A non-Success verdict
        // is a failed post that merely arrived intact, so it must not Ack the row. Retriable: the deterministic
        // key is replayed verbatim, so LN dedupes if the retry turns out to be a duplicate.
        //
        // Code-owned, not expression-owned, for the same reason as the HTTP classifier (D-R9-5): no admin-edited
        // mapping may decide whether a row is finished.
        //
        // !! Success here means "LN parsed the request", NOT "LN applied it". Probe run 3 (2026-07-29) posted
        // POStatus:"Zzz" and got Status:"Success" back — LN does not validate that field. This is the strongest
        // signal the envelope offers; it is not proof of application.
        if (IsPoUpdateTransaction(row.TransactionType)
            && !string.Equals(ack.ErpStatus, "Success", StringComparison.OrdinalIgnoreCase))
        {
            var detail = string.IsNullOrWhiteSpace(ack.Message) ? "no remarks returned" : ack.Message;
            return Retriable(row,
                $"[{route.PortalEntity}] LN accepted the request (HTTP {outcome.StatusCode}) but reported "
                + $"'{ack.ErpStatus}': {detail}", canonicalBody);
        }

        // --- 7. D-R9-20 — erpStatus → the entity's existing ERP-owned status column (PO responses only today). ----
        // PoNegotiationApprove is absent by necessity, not oversight: its EntityId is the NEGOTIATION id, so this
        // update would match no PurchaseOrder row. PoAcknowledge is gone with the transaction (R12/D14).
        if (row.TransactionType is OutboxTransactionType.PoAccept or OutboxTransactionType.PoReject)
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

    /// <summary>R12 — the three transactions that post the LN <c>PO_Update</c> contract (D13).</summary>
    private static bool IsPoUpdateTransaction(string transactionType)
        => transactionType is OutboxTransactionType.PoAccept
            or OutboxTransactionType.PoReject
            or OutboxTransactionType.PoNegotiationApprove;

    /// <summary>
    /// R12 (D9) — does the input document carry a usable <c>companyCode</c>? Read off the code-owned input
    /// document rather than the mapped body on purpose: the body's shape is admin-editable, this guard is not.
    /// A document that will not even parse fails closed (treated as "no company").
    /// </summary>
    private static bool HasCompanyCode(string inputJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
            return doc.RootElement.TryGetProperty("companyCode", out var el)
                   && el.ValueKind == System.Text.Json.JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(el.GetString());
        }
        catch { return false; }
    }

    private static LnDispatchOutcome Permanent(OutboxMessage row, string message)
        => new(new InforSyncResult(false, row.DeterministicKey, message), true);

    private static LnDispatchOutcome Retriable(OutboxMessage row, string message, string? payloadJson)
        => new(new InforSyncResult(false, row.DeterministicKey, message, payloadJson), false);

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
