using System.Security.Cryptography;
using System.Text;

namespace MerinoOne.SupplierPortal.Application.Common.Integration;

/// <summary>
/// Backend-owned constant strings for the outbound transactional outbox (Increment 0). These are NOT
/// in Enums.cs (the solution-architect owns that file) — the outbox <c>transactionType</c>/<c>entityName</c>
/// values are an application-layer concern (the dispatcher routes on them). APPEND-ONLY: existing values are
/// persisted on live <c>integration.OutboxMessage</c> rows, so never rename or repurpose one.
/// </summary>
public static class OutboxTransactionType
{
    public const string PoAcknowledge   = "PoAcknowledge";
    public const string PoAccept        = "PoAccept";
    public const string PoReject        = "PoReject";
    public const string AsnPost         = "AsnPost";
    public const string InvoicePost     = "InvoicePost";
    public const string SupplierChange  = "SupplierChange";
    public const string SupplierSync    = "SupplierSync";
}

/// <summary>
/// The portal entity an outbox row concerns. Doubles as the <c>InforSyncLog.EntityName</c> and the
/// dispatcher's routing discriminator (matched to the right <c>IInforIntegrationService</c> method).
/// APPEND-ONLY.
/// </summary>
public static class OutboxEntity
{
    public const string PurchaseOrder  = "PurchaseOrder";
    public const string Asn            = "Asn";
    public const string Invoice        = "Invoice";
    public const string Supplier       = "Supplier";
    public const string SupplierChange = "SupplierChange";
}

/// <summary>
/// Deterministic outbound idempotency key (Increment 0). Key = <c>sha256("&lt;entity&gt;|&lt;businessKey&gt;|&lt;op&gt;")</c>,
/// REUSED verbatim across retries so the ERP dedupes (it doubles as the correlation id / <c>portalRef</c> echoed
/// back on <c>/inbound/erp-ack</c>). The filtered-unique index <c>UQ_OutboxMessage_deterministicKey</c> backs
/// exactly-once enqueue. NEVER mint a fresh GUID for an outbound idempotency key — that is the D2 defect this fixes.
/// </summary>
public static class OutboxKey
{
    public static string For(string entity, string businessKey, string op)
    {
        var raw = $"{entity}|{businessKey}|{op}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
