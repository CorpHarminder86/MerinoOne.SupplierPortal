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
/// the <c>/inbound/erp-ack</c> endpoint. The enqueue-idempotency unique index is tenant-qualified
/// (<c>UQ_OutboxMessage_tenant_deterministicKey ON (tenantId, deterministicKey) WHERE [isDeleted]=0</c>) because
/// <see cref="DeterministicKey"/> is only unique per tenant+supplier (migration 0023, review B2).</para>
///
/// <para><b>Atomic claim (migration 0023, review B1):</b> the outbox carries a <see cref="RowVersion"/>
/// concurrency token (<see cref="IHasRowVersion"/>) so the dispatcher can claim a row
/// (<c>Pending → Sending</c>) and commit BEFORE the ERP POST — a crash/restart or a second instance then loses
/// the optimistic-concurrency race rather than re-POSTing. No ERP HTTP call runs against an unclaimed row.</para>
/// </summary>
public class OutboxMessage : AuditableEntity, ITenantOwned, IHasRowVersion
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

    /// <summary>
    /// SQL <c>rowversion</c> optimistic-concurrency token (migration 0023, review B1). Mapped by the
    /// global <see cref="IHasRowVersion"/> convention in <c>AppDbContext</c> (<c>.IsRowVersion()</c> →
    /// column <c>rowVersion</c>). Backs the per-row dispatcher claim (<c>Pending → Sending</c> committed
    /// before the ERP POST).
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
