using MerinoOne.SupplierPortal.Domain.Common;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// Transactional outbox (Increment 0). A local state change and an <see cref="OutboxMessage"/> row commit in
/// ONE transaction; a post-commit dispatcher (backend-developer) then calls the ERP method, writes the
/// <see cref="InforSyncLog"/> and — on failure — an <see cref="IntegrationError"/> (retryable). No ERP HTTP
/// call ever holds a DB transaction open. This is integration infrastructure: tenant-scoped (<see cref="ITenantOwned"/>),
/// NOT seccode-protected (no <see cref="ISeccode"/> — it carries cross-tenant correlation handles, not business rows).
///
/// <para><b>Idempotency:</b> <see cref="DeterministicKey"/> is <c>sha256("&lt;entity&gt;|&lt;businessKey&gt;|&lt;op&gt;")</c>,
/// reused across retries so ERP dedupes; it doubles as the correlation id / <c>portalRef</c> echoed back by ERP on
/// the <c>/inbound/erp-ack</c> endpoint. A filtered-unique index on it enforces enqueue idempotency.</para>
/// </summary>
public class OutboxMessage : AuditableEntity, ITenantOwned
{
    /// <summary>Owning tenant — the outbox is tenant-scoped (covered by the always-on tenant filter).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>The outbound operation, e.g. <c>AsnPost</c>, <c>InvoicePost</c>, <c>SupplierChangePush</c>, <c>PoAccept</c>.</summary>
    public string TransactionType { get; set; } = string.Empty;

    /// <summary>The portal entity this message concerns, e.g. <c>Asn</c>, <c>Invoice</c>, <c>SupplierChange</c>.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>The portal entity row id (nullable — some transactions key only on the deterministic key).</summary>
    public Guid? EntityId { get; set; }

    /// <summary>
    /// Deterministic ERP correlation id / <c>portalRef</c>. Reused verbatim across retries (NOT a fresh GUID per
    /// call) so ERP dedupes. Filtered-unique on live rows enforces enqueue idempotency.
    /// </summary>
    public string DeterministicKey { get; set; } = string.Empty;

    /// <summary>Serialized request body for the dispatcher. Null when the dispatcher rebuilds the payload from the entity.</summary>
    public string? PayloadJson { get; set; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public DateTime? AckedAt { get; set; }
    public string? LastError { get; set; }
}
