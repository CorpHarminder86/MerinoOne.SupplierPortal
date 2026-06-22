using MerinoOne.SupplierPortal.Application.Common.Interfaces;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Outbox;

/// <summary>
/// Scoped implementation of <see cref="IOutboundIdempotencyContext"/>. One instance per DI scope holds the
/// deterministic key for the in-flight outbound call. The dispatcher sets it before calling the ERP method and
/// clears it after; the Live integration service reads <see cref="CurrentKey"/> and replays it as the ERP
/// idempotency header.
/// </summary>
public sealed class OutboundIdempotencyContext : IOutboundIdempotencyContext
{
    public string? CurrentKey { get; private set; }
    public void Set(string deterministicKey) => CurrentKey = deterministicKey;
    public void Clear() => CurrentKey = null;
}
