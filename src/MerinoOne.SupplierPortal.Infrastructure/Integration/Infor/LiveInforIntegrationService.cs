using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Live outbound Infor LN integration over ION REST (OData JSON). Per call it:
///   1. resolves the current tenant's connection (<see cref="IInforConnectionProvider"/>),
///   2. obtains a cached bearer token (<see cref="IInforTokenProvider"/>),
///   3. builds the per-entity payload + endpoint path,
///   4. POSTs JSON with Authorization: Bearer + X-Infor-LnCompany, and
///   5. maps the HTTP outcome to <see cref="InforSyncResult"/>.
///
/// The call sites (Acknowledge/Accept/Reject PO, Submit invoice/ASN, Sync supplier) still own the
/// <c>InforSyncLog</c> write — this service only performs the call and returns the result, exactly
/// like the mock it replaces.
///
/// ──────────────────────────────────────────────────────────────────────────────────────────────
/// TODO (per-tenant Infor LN spec — NOT in the repo): the endpoint relative paths in
/// <see cref="EndpointPaths"/> and the field maps in each <c>Build*Payload</c> method below are
/// STARTER values. Replace them with the real ION REST hook / OData entity paths and the exact field
/// mappings agreed with your Infor LN team before enabling Integration:Mode=Live in production.
/// ──────────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
public class LiveInforIntegrationService : IInforIntegrationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // TODO: replace with the real ION REST / OData paths (appended to InforConnectionSetting.ApiBaseUrl).
    private static class EndpointPaths
    {
        public const string Supplier = "LN/lnapi/odata/tdapi.bpSuppliers/Suppliers";
        public const string PurchaseOrderAck = "LN/lnapi/odata/tdapi.purchaseOrders/Acknowledgements";
        public const string PurchaseOrderAccept = "LN/lnapi/odata/tdapi.purchaseOrders/Acceptances";
        public const string PurchaseOrderReject = "LN/lnapi/odata/tdapi.purchaseOrders/Rejections";
        public const string Invoice = "LN/lnapi/odata/cisli.selfBillingInvoices/Invoices";
        public const string Asn = "LN/lnapi/odata/whinh.advanceShipmentNotices/Asns";
        public const string SupplierChange = "LN/lnapi/odata/tdapi.bpSuppliers/SupplierChanges";
        // TODO: confirm real Infor LN BOD path + field map (PO-negotiation cutover — see memory `infor-live-cutover-checklist`).
        public const string PoNegotiation = "LN/lnapi/odata/tdapi.purchaseOrders/Negotiations";
    }

    private readonly IInforConnectionProvider _connections;
    private readonly IInforTokenProvider _tokens;
    private readonly IAppDbContext _db;
    private readonly IOutboundIdempotencyContext _idempotency;
    private readonly ILogger<LiveInforIntegrationService> _logger;

    public LiveInforIntegrationService(
        IInforConnectionProvider connections,
        IInforTokenProvider tokens,
        IAppDbContext db,
        IOutboundIdempotencyContext idempotency,
        ILogger<LiveInforIntegrationService> logger)
    {
        _connections = connections;
        _tokens = tokens;
        _db = db;
        _idempotency = idempotency;
        _logger = logger;
    }

    public async Task<InforSyncResult> SyncSupplierAsync(Guid supplierId, CancellationToken ct = default)
    {
        // R4 Module 1 — the supplier body (header + addresses[]/contacts[]/bankDetails[]/licenses[]) is built by the
        // shared SupplierOutboundPayloadBuilder so Mock and Live POST/log the identical canonical payload. The builder
        // uses IgnoreQueryFilters (this can run under the tenant-less background dispatcher).
        var payload = await SupplierOutboundPayloadBuilder.BuildPayloadAsync(_db, supplierId, ct);
        if (payload is null) return Fail("Supplier", $"Supplier {supplierId} not found.");
        return await SendAsync("Supplier", EndpointPaths.Supplier, payload, ct);
    }

    public async Task<InforSyncResult> AcknowledgePurchaseOrderAsync(Guid purchaseOrderId, CancellationToken ct = default)
    {
        var po = await _db.PurchaseOrders.FindAsync(new object?[] { purchaseOrderId }, ct);
        if (po is null) return Fail("PurchaseOrder", $"PurchaseOrder {purchaseOrderId} not found.");

        // TODO: confirm the real LN PO-acknowledgement field map.
        var payload = new
        {
            PoNumber = po.PoNumber,
            Action = "Acknowledge",
            AcknowledgedAt = (po.AcknowledgmentAt ?? DateTime.UtcNow).ToString("o"),
        };
        return await SendAsync("PurchaseOrder", EndpointPaths.PurchaseOrderAck, payload, ct);
    }

    public async Task<InforSyncResult> AcceptPurchaseOrderAsync(Guid purchaseOrderId, DateTime? proposedDate, CancellationToken ct = default)
    {
        var po = await _db.PurchaseOrders.FindAsync(new object?[] { purchaseOrderId }, ct);
        if (po is null) return Fail("PurchaseOrder", $"PurchaseOrder {purchaseOrderId} not found.");

        // TODO: confirm the real LN PO-acceptance / date-proposal field map.
        var payload = new
        {
            PoNumber = po.PoNumber,
            Action = "Accept",
            ProposedDeliveryDate = (proposedDate ?? po.ProposedDeliveryDate)?.ToString("o"),
        };
        return await SendAsync("PurchaseOrder", EndpointPaths.PurchaseOrderAccept, payload, ct);
    }

    public async Task<InforSyncResult> RejectPurchaseOrderAsync(Guid purchaseOrderId, string reason, CancellationToken ct = default)
    {
        var po = await _db.PurchaseOrders.FindAsync(new object?[] { purchaseOrderId }, ct);
        if (po is null) return Fail("PurchaseOrder", $"PurchaseOrder {purchaseOrderId} not found.");

        // TODO: confirm the real LN PO-rejection field map.
        var payload = new
        {
            PoNumber = po.PoNumber,
            Action = "Reject",
            Reason = reason,
        };
        return await SendAsync("PurchaseOrder", EndpointPaths.PurchaseOrderReject, payload, ct);
    }

    public async Task<InforSyncResult> SubmitInvoiceAsync(Guid invoiceId, CancellationToken ct = default)
    {
        // R4 — the invoice body is built by the shared InvoiceOutboundPayloadBuilder so Mock and Live POST/log the
        // identical canonical payload. The builder uses IgnoreQueryFilters (this can run under the tenant-less
        // background dispatcher).
        var payload = await InvoiceOutboundPayloadBuilder.BuildPayloadAsync(_db, invoiceId, ct);
        if (payload is null) return Fail("Invoice", $"Invoice {invoiceId} not found.");
        return await SendAsync("Invoice", EndpointPaths.Invoice, payload, ct);
    }

    public async Task<InforSyncResult> SubmitAsnAsync(Guid asnId, CancellationToken ct = default)
    {
        // R4 (2026-06-23) — the ASN body (header + lines[] with serials[]/lots[]) is built by the shared
        // AsnOutboundPayloadBuilder so Mock and Live POST/log the identical canonical payload. The serialized
        // JSON SendAsync sends is surfaced on the result so the dispatcher persists it to InforSyncLog.PayloadJson.
        var payload = await AsnOutboundPayloadBuilder.BuildPayloadAsync(_db, asnId, ct);
        if (payload is null) return Fail("Asn", $"ASN {asnId} not found.");
        return await SendAsync("Asn", EndpointPaths.Asn, payload, ct);
    }

    // R4 Module 2 — pushes an APPROVED supplier change request to LN. Per the plan, it sends the FULL intended
    // end-state per erpCode-keyed entity (the live row after the deltas were applied) — NOT a since-last delta —
    // so LN can upsert each entity by its erpCode. The change request's lines tell us WHICH live rows changed; we
    // resolve each to its current state and project it into the payload. The deterministic outbox key (replayed via
    // IOutboundIdempotencyContext in SendAsync) is the correlation id LN echoes back on /inbound/erp-ack to stamp
    // each line's erpRef. The OutboxDispatcherWorker owns the InforSyncLog/IntegrationError write on the result.
    public async Task<InforSyncResult> SubmitSupplierChangeAsync(Guid changeRequestId, CancellationToken ct = default)
    {
        // R4 — the supplier-change body (the full intended end-state per erpCode-keyed entity the change touched) is
        // built by the shared SupplierChangeOutboundPayloadBuilder so Mock and Live POST/log the identical canonical
        // payload. The builder uses IgnoreQueryFilters (this runs under the tenant-less background dispatcher).
        var payload = await SupplierChangeOutboundPayloadBuilder.BuildPayloadAsync(_db, changeRequestId, ct);
        if (payload is null) return Fail("SupplierChange", $"SupplierChangeRequest {changeRequestId} (or its supplier) not found.");
        return await SendAsync("SupplierChange", EndpointPaths.SupplierChange, payload, ct);
    }

    // R4 (2026-06-24) — pushes a buyer-APPROVED PO negotiation (revised qty / delivery dates) to LN. The body is
    // built by the shared PoNegotiationOutboundPayloadBuilder so Mock and Live POST/log the identical canonical
    // payload. The builder uses IgnoreQueryFilters (this runs under the tenant-less background dispatcher). The
    // deterministic outbox key (replayed via IOutboundIdempotencyContext in SendAsync) is the correlation id LN
    // echoes back on /inbound/erp-ack. The OutboxDispatcherWorker owns the InforSyncLog write on the result.
    public async Task<InforSyncResult> ApprovePoNegotiationAsync(Guid negotiationId, CancellationToken ct = default)
    {
        var payload = await PoNegotiationOutboundPayloadBuilder.BuildPayloadAsync(_db, negotiationId, ct);
        if (payload is null) return Fail("PoNegotiation", $"PurchaseOrderNegotiation {negotiationId} not found.");
        return await SendAsync("PoNegotiation", EndpointPaths.PoNegotiation, payload, ct);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────────────────────

    private async Task<InforSyncResult> SendAsync(string entity, string relativePath, object payload, CancellationToken ct)
    {
        // Serialize once, up front, so the canonical "what we sent" body is captured on EVERY outcome (success,
        // ERP-reject, transport error, and even pre-flight config failures) and the dispatcher can persist it to
        // InforSyncLog.PayloadJson regardless of whether the POST actually left the building.
        var bodyJson = JsonSerializer.Serialize(payload, JsonOpts);

        var conn = await _connections.GetCurrentAsync(ct);
        if (conn is null || !conn.IsActive)
            return Fail(entity, "Infor connection is not configured (or is disabled) for this tenant.", bodyJson);
        if (!conn.IsConfigured)
            return Fail(entity, "Infor connection is incomplete — set Access Token URL, ION API Base URL and Company in Settings.", bodyJson);

        var token = await _tokens.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            return Fail(entity, "Could not obtain an Infor access token — re-test the connection in Settings.", bodyJson);

        if (!TryBuildUrl(conn.ApiBaseUrl, relativePath, out var url))
            return Fail(entity, $"Could not build a valid endpoint URL from ION API Base URL '{conn.ApiBaseUrl}'.", bodyJson);

        // Replay the deterministic outbox key (reused verbatim across retries so LN dedupes — fixes D2). Only
        // legacy direct calls with no ambient key fall back to a fresh GUID.
        var idempotencyKey = _idempotency.CurrentKey ?? Guid.NewGuid().ToString("N");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(conn.PrimaryCompany))
                req.Headers.TryAddWithoutValidation("X-Infor-LnCompany", conn.PrimaryCompany);
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return new InforSyncResult(true, idempotencyKey, $"{entity} accepted by Infor (HTTP {(int)resp.StatusCode}).", bodyJson);

            var body = await resp.Content.ReadAsStringAsync(ct);
            return Fail(entity, $"Infor rejected the request (HTTP {(int)resp.StatusCode}): {Truncate(body, 300)}", bodyJson);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(entity, "Infor request timed out.", bodyJson);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Infor {Entity} request failed (transport).", entity);
            return Fail(entity, $"Could not reach Infor: {ex.Message}", bodyJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Infor {Entity} request failed.", entity);
            return Fail(entity, ex.Message, bodyJson);
        }
    }

    /// <summary>Joins the configured ION API base URL with a relative path (tolerant of trailing/leading slashes).</summary>
    private static bool TryBuildUrl(string apiBaseUrl, string relativePath, out string url)
    {
        url = string.Empty;
        if (!Uri.TryCreate(apiBaseUrl?.Trim(), UriKind.Absolute, out var baseUri)) return false;
        var basePart = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var rel = relativePath.Trim().TrimStart('/');
        url = $"{basePart}/{rel}";
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    private static InforSyncResult Fail(string entity, string message, string? payloadJson = null) => new(false, null, $"[{entity}] {message}", payloadJson);

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
