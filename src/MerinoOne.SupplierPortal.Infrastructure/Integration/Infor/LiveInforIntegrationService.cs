using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
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
    }

    private readonly IInforConnectionProvider _connections;
    private readonly IInforTokenProvider _tokens;
    private readonly IAppDbContext _db;
    private readonly ILogger<LiveInforIntegrationService> _logger;

    public LiveInforIntegrationService(
        IInforConnectionProvider connections,
        IInforTokenProvider tokens,
        IAppDbContext db,
        ILogger<LiveInforIntegrationService> logger)
    {
        _connections = connections;
        _tokens = tokens;
        _db = db;
        _logger = logger;
    }

    public async Task<InforSyncResult> SyncSupplierAsync(Guid supplierId, CancellationToken ct = default)
    {
        var supplier = await _db.Suppliers.FindAsync(new object?[] { supplierId }, ct);
        if (supplier is null) return Fail("Supplier", $"Supplier {supplierId} not found.");

        // TODO: confirm the real LN supplier (business partner) field map.
        var payload = new
        {
            SupplierCode = supplier.SupplierCode,
            Name = supplier.LegalName,
            TradeName = supplier.TradeName,
            GstNumber = supplier.GstNumber,
            PanNumber = supplier.PanNumber,
            IsActive = supplier.IsActiveSupplier,
        };
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
        var invoice = await _db.Invoices.FindAsync(new object?[] { invoiceId }, ct);
        if (invoice is null) return Fail("Invoice", $"Invoice {invoiceId} not found.");

        // TODO: confirm the real LN self-billing / invoice field map (incl. lines if required).
        var payload = new
        {
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate.ToString("o"),
            Currency = invoice.CurrencyCode,
            InvoiceAmount = invoice.InvoiceAmount,
            TaxAmount = invoice.TaxAmount,
            NetAmount = invoice.NetAmount,
            EInvoiceIrn = invoice.EInvoiceIrn,
        };
        return await SendAsync("Invoice", EndpointPaths.Invoice, payload, ct);
    }

    public async Task<InforSyncResult> SubmitAsnAsync(Guid asnId, CancellationToken ct = default)
    {
        var asn = await _db.Asns.FindAsync(new object?[] { asnId }, ct);
        if (asn is null) return Fail("Asn", $"ASN {asnId} not found.");

        // TODO: confirm the real LN advance-shipment-notice field map (incl. lines if required).
        var payload = new
        {
            AsnNumber = asn.AsnNumber,
            ExpectedDeliveryDate = asn.ExpectedDeliveryDate.ToString("o"),
            CarrierName = asn.CarrierName,
            TrackingNumber = asn.TrackingNumber,
            VehicleNumber = asn.VehicleNumber,
        };
        return await SendAsync("Asn", EndpointPaths.Asn, payload, ct);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────────────────────

    private async Task<InforSyncResult> SendAsync(string entity, string relativePath, object payload, CancellationToken ct)
    {
        var conn = await _connections.GetCurrentAsync(ct);
        if (conn is null || !conn.IsActive)
            return Fail(entity, "Infor connection is not configured (or is disabled) for this tenant.");
        if (!conn.IsConfigured)
            return Fail(entity, "Infor connection is incomplete — set Access Token URL, ION API Base URL and Company in Settings.");

        var token = await _tokens.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            return Fail(entity, "Could not obtain an Infor access token — re-test the connection in Settings.");

        if (!TryBuildUrl(conn.ApiBaseUrl, relativePath, out var url))
            return Fail(entity, $"Could not build a valid endpoint URL from ION API Base URL '{conn.ApiBaseUrl}'.");

        var idempotencyKey = Guid.NewGuid().ToString("N");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(conn.PrimaryCompany))
                req.Headers.TryAddWithoutValidation("X-Infor-LnCompany", conn.PrimaryCompany);
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return new InforSyncResult(true, idempotencyKey, $"{entity} accepted by Infor (HTTP {(int)resp.StatusCode}).");

            var body = await resp.Content.ReadAsStringAsync(ct);
            return Fail(entity, $"Infor rejected the request (HTTP {(int)resp.StatusCode}): {Truncate(body, 300)}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(entity, "Infor request timed out.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Infor {Entity} request failed (transport).", entity);
            return Fail(entity, $"Could not reach Infor: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Infor {Entity} request failed.", entity);
            return Fail(entity, ex.Message);
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

    private static InforSyncResult Fail(string entity, string message) => new(false, null, $"[{entity}] {message}");

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
