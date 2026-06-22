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
/// Deterministic outbound idempotency key (Increment 0, review B2). Key =
/// <c>sha256("&lt;tenantId:N&gt;|&lt;entity&gt;|&lt;businessKey&gt;|&lt;op&gt;")</c>, REUSED verbatim across retries so the ERP
/// dedupes (it doubles as the correlation id / <c>portalRef</c> echoed back on <c>/inbound/erp-ack</c>). The
/// composite filtered-unique index <c>UQ_OutboxMessage_tenant_deterministicKey</c> on <c>(tenantId,
/// deterministicKey)</c> backs exactly-once enqueue.
///
/// <para><b>Review B2 — tenant-qualification is mandatory.</b> A business key like <c>InvoiceNumber</c> is unique
/// only per <c>(SupplierId, InvoiceNumber)</c> within a tenant (and certainly NOT across tenants), so the key
/// MUST fold the <c>tenantId</c> (and, for invoices, the <c>supplierId</c>) into the hashed material. Two tenants
/// — or two suppliers — sharing the same <c>InvoiceNumber</c> would otherwise collapse onto one key and one
/// tenant would be marked posted with no outbox row (never paid) or DoS the other's batch on the unique index.
/// Because the key doubles as the erp-ack <c>portalRef</c> correlation id, it MUST be tenant-unique.</para>
///
/// <para>NEVER mint a fresh GUID for an outbound idempotency key — that is the D2 defect this fixes.</para>
/// </summary>
public static class OutboxKey
{
    /// <summary>
    /// Tenant-qualified deterministic key (review B2). Always prefer this overload — every live outbox key must be
    /// tenant-unique. <paramref name="businessKey"/> carries the per-entity discriminator: for invoices it MUST be
    /// supplier-qualified (e.g. <c>$"{supplierId:N}|{invoiceNumber}"</c>); for PO/ASN/SupplierChange the
    /// entity-local business key (PoNumber/AsnNumber/changeRequestId) is already unique within the tenant.
    /// </summary>
    public static string For(string entity, Guid? tenantId, string businessKey, string op)
        => Hash($"{tenantId.GetValueOrDefault():N}|{entity}|{businessKey}|{op}");

    private static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
