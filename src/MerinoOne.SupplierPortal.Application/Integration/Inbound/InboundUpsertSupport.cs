using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Integration.Inbound;

/// <summary>
/// Shared helpers for the inbound master-data upsert path (Payment Term / Delivery Term). Centralizes
/// the canonical payload hashing used for idempotency and the EntityName mapping so the two commands
/// stay in lock-step.
/// </summary>
public static class InboundUpsertSupport
{
    /// <summary>InforSyncLog.EntityId cap (matches the nvarchar(400) column).</summary>
    public const int EntityIdMaxLength = 400;

    /// <summary>
    /// PayloadJson byte cap (~64 KB). Protects the SQL-Express 10 GB cap from a runaway batch body. A
    /// larger payload is replaced by a small truncation marker rather than truncated mid-JSON (which would
    /// be unparseable in the viewer).
    /// </summary>
    public const int PayloadMaxBytes = 64 * 1024;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new() { WriteIndented = false };

    /// <summary>SQL Server error numbers for a unique-index / primary-key violation.</summary>
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlPrimaryKeyViolation = 2627;

    /// <summary>
    /// The enqueue-idempotency index whose violation IS the benign concurrent-duplicate signal — the loser of a
    /// concurrent GRN-auto-post / outbox-enqueue race hitting the composite outbox UQ at commit. SQL 2601/2627 name
    /// the violated index in the error message; ONLY a collision on this index (review D3) is treated as benign.
    /// </summary>
    private const string OutboxEnqueueUniqueIndex = "UQ_OutboxMessage_tenant_deterministicKey";

    /// <summary>
    /// Review S1/D3 — true ONLY when <paramref name="ex"/> is the PRECISE benign, RETRYABLE write conflict from a
    /// concurrent / duplicate inbound delivery: the loser of an outbox enqueue race hitting
    /// <c>UQ_OutboxMessage_tenant_deterministicKey</c> at commit, OR an optimistic-concurrency clash whose affected
    /// entries are ALL <c>OutboxMessage</c> rows (the enqueue/claim race). The executor converts that into a
    /// retryable per-row skip + a Failed SyncLog and returns 200, so LN re-delivers later instead of retrying the
    /// whole batch.
    ///
    /// <para><b>Review D3 — the benign skip is NO LONGER batch-wide.</b> Previously ANY unique-index/PK violation —
    /// including a genuine corruption in an unrelated receipt of the batch — was swallowed as a benign Skip and
    /// looped forever on re-delivery. Now a unique violation on any index OTHER than the outbox enqueue UQ (and a
    /// concurrency clash on a non-<c>OutboxMessage</c> entity) surfaces as a REAL error (500 / per-row Failed), as
    /// it should. The genuine claim-lost-at-commit case (the only safe-to-skip race) stays narrow and precise.</para>
    /// </summary>
    public static bool IsRetryableConcurrencyOrUniqueViolation(Exception ex)
    {
        // Optimistic-concurrency clash: benign ONLY when every affected entry is an OutboxMessage (the enqueue /
        // dispatcher-claim race). A RowVersion clash on an Invoice / GoodsReceipt / Payment is a real conflict.
        if (ex is DbUpdateConcurrencyException dbc)
            return dbc.Entries.Count > 0
                   && dbc.Entries.All(e => e.Entity is Domain.Entities.Integration.OutboxMessage);

        // Unique/PK violation: benign ONLY when the violated index is the outbox enqueue UQ. Walk the exception
        // chain for the provider SqlException (DbUpdateException wraps it as InnerException).
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SqlException sql)
            {
                var uniqueOrPk = sql.Errors.Cast<SqlError>()
                    .Where(e => e.Number is SqlUniqueIndexViolation or SqlPrimaryKeyViolation)
                    .ToList();
                if (uniqueOrPk.Count == 0) continue;
                // SQL names the violated index/constraint in the error text; require it to be the outbox enqueue UQ.
                return uniqueOrPk.Any(e =>
                    e.Message.Contains(OutboxEnqueueUniqueIndex, StringComparison.OrdinalIgnoreCase));
            }
        }
        return false;
    }

    /// <summary>The InforEndpointMap.EntityName used for the inbound endpoint gate + session telemetry.</summary>
    public static string EntityName(SharedEndpoint endpoint) => endpoint.ToString();

    /// <summary>EntityName for the tenant-scoped inbound masters (Currency/Country/State/City/PostalCode).</summary>
    public static string EntityName(TenantInboundEntity endpoint) => endpoint.ToString();

    /// <summary>
    /// R4 (2026-06-22) — Module 5 / Increment D. EntityName for the transactional inbound entities
    /// (Grn/Payment/InvoiceStatus/ErpAck). Drives the <c>InforEndpointMap.EntityName</c> endpoint-gate +
    /// session telemetry on the /inbound/grn-status, /inbound/payments, /inbound/invoice-status and
    /// /inbound/erp-ack endpoints. Mirrors the existing <see cref="SharedEndpoint"/>/<see cref="TenantInboundEntity"/>
    /// overloads.
    /// </summary>
    public static string EntityName(TransactionalInboundEntity endpoint) => endpoint.ToString();

    /// <summary>
    /// SHA-256 (hex) of a stable canonical projection of the batch, keyed on an arbitrary scope string
    /// (e.g. "Country|&lt;tenantGuid&gt;"). Tenant-path equivalent of the company-scoped <see cref="CanonicalHash(SharedEndpoint, Guid, IEnumerable{string})"/>.
    /// </summary>
    public static string CanonicalHash(string scopeKey, IEnumerable<string> canonicalRows)
    {
        var sb = new StringBuilder();
        sb.Append(scopeKey).Append('|');
        foreach (var row in canonicalRows.OrderBy(r => r, StringComparer.Ordinal))
            sb.Append(row).Append(';');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Comma-joins the batch's term codes for <c>InforSyncLog.EntityId</c>, capped to
    /// <see cref="EntityIdMaxLength"/> chars (column width). When truncated, the tail is replaced by a
    /// <c>…(+N more)</c> marker so the count is still legible.
    /// </summary>
    public static string? JoinCodes(IEnumerable<string> codes)
    {
        var list = codes?
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList() ?? new List<string>();

        if (list.Count == 0) return null;

        var joined = string.Join(",", list);
        if (joined.Length <= EntityIdMaxLength) return joined;

        // Truncate on a code boundary and append a "+N more" marker that fits inside the cap.
        var kept = new StringBuilder();
        var keptCount = 0;
        foreach (var code in list)
        {
            var addition = (kept.Length == 0 ? 0 : 1) + code.Length;
            // Reserve ~20 chars for the marker.
            if (kept.Length + addition > EntityIdMaxLength - 20) break;
            if (kept.Length > 0) kept.Append(',');
            kept.Append(code);
            keptCount++;
        }

        var remaining = list.Count - keptCount;
        var result = $"{kept}…(+{remaining} more)";
        return result.Length > EntityIdMaxLength ? result[..EntityIdMaxLength] : result;
    }

    /// <summary>
    /// Serializes the inbound request to JSON for <c>InforSyncLog.PayloadJson</c>, guarded to
    /// <see cref="PayloadMaxBytes"/>. An oversize body is replaced by a
    /// <c>{"_truncated":true,"_bytes":N}</c> marker (never the full payload). Never throws — on a
    /// serialization failure a small error marker is stored instead.
    /// </summary>
    public static string? SerializePayloadCapped(object? payload)
    {
        if (payload is null) return null;
        try
        {
            var json = JsonSerializer.Serialize(payload, PayloadJsonOptions);
            var bytes = Encoding.UTF8.GetByteCount(json);
            if (bytes <= PayloadMaxBytes)
                return json;

            return $"{{\"_truncated\":true,\"_bytes\":{bytes}}}";
        }
        catch
        {
            return "{\"_truncated\":true,\"_error\":\"serialization_failed\"}";
        }
    }

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
