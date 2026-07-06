using MerinoOne.SupplierPortal.Domain.Common;

namespace MerinoOne.SupplierPortal.Domain.Entities.Integration;

/// <summary>
/// R9 (TSD R9 §2.6, D-R9-11 inbound scope) — accept-and-hold store for /inbound/erp-ack under an
/// <c>InboundErpAck</c> kill: the raw ack body persists here (HTTP 200 to LN — never 503) and the
/// <c>HeldInboundReplayWorker</c> replays strictly FIFO (clustered Seq order) once the switch re-enables.
/// The original idempotency key is replayed verbatim, so an ack that already processed pre-kill dedupes
/// via the executor's prior-Success check. After <see cref="MaxReplayAttempts"/> failures the row goes
/// <c>Failed</c> + one IntegrationError (operator surface).
/// </summary>
public class HeldInboundMessage : AuditableEntity, ITenantOwned
{
    public const int MaxReplayAttempts = 5;

    public Guid? TenantId { get; set; }

    /// <summary>Inbound endpoint discriminator — today always <c>ErpAck</c> (future holds reuse the store).</summary>
    public string EndpointName { get; set; } = "ErpAck";

    /// <summary>The raw inbound request body (one PushErpAckRequest batch), verbatim.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>The inbound call's idempotency key (replayed verbatim so pre-kill processing dedupes).</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>The API key's bound company ids at receipt time (anti-spoof context survives the hold).</summary>
    public string? BoundCompanyIdsJson { get; set; }

    /// <summary>CHECK-constrained: <c>Held</c> → <c>Replayed</c> | <c>Failed</c> (after MaxReplayAttempts).</summary>
    public string Status { get; set; } = "Held";

    public int ReplayAttempts { get; set; }
    public DateTime? ReplayedOn { get; set; }
    public string? LastError { get; set; }
}
