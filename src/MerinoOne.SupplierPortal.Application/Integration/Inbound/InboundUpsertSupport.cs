using System.Security.Cryptography;
using System.Text;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// Shared helpers for the inbound master-data upsert path (Payment Term / Delivery Term). Centralizes
/// the canonical payload hashing used for idempotency and the EntityName mapping so the two commands
/// stay in lock-step.
/// </summary>
public static class InboundUpsertSupport
{
    /// <summary>The InforEndpointMap.EntityName used for the inbound endpoint gate + session telemetry.</summary>
    public static string EntityName(SharedEndpoint endpoint) => endpoint.ToString();

    /// <summary>
    /// SHA-256 (hex) of a stable, canonical projection of the batch. Used as the idempotency key when the
    /// caller does not supply an <c>Idempotency-Key</c> header — a replay of the identical body short-circuits.
    /// The source company is folded in so the same body received under two different companies is NOT treated
    /// as a replay.
    /// </summary>
    public static string CanonicalHash(SharedEndpoint endpoint, Guid sourceId, IEnumerable<string> canonicalRows)
    {
        var sb = new StringBuilder();
        sb.Append(endpoint).Append('|').Append(sourceId.ToString("N")).Append('|');
        // Deterministic order so row reordering doesn't change the hash.
        foreach (var row in canonicalRows.OrderBy(r => r, StringComparer.Ordinal))
            sb.Append(row).Append(';');

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
