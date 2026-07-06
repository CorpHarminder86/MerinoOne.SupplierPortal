using System.Text.Json;
using MerinoOne.SupplierPortal.Contracts.Integration;

namespace MerinoOne.SupplierPortal.Application.Integration.Ln;

/// <summary>
/// R9 (TSD R9 §2.3, D-R9-4) — validator/parser for the fixed CLOSED response/ack contract:
/// <c>{ erpKey: string, erpStatus: string, message?: string, correlationBag?: object }</c>.
/// CLOSED means closed: any unknown key is an error naming the key. Enforced at config save
/// (against the pinned samples — unknown keys BLOCK save, never discovered at dispatch) and again
/// at dispatch on the mapped response before anything is written.
/// </summary>
public static class LnClosedContract
{
    private static readonly string[] KnownKeys = { "erpKey", "erpStatus", "message", "correlationBag" };

    /// <summary>Parses mapped output into the closed contract. Errors are exhaustive (all problems reported, not first-only).</summary>
    public static (LnOutboundAck? Ack, IReadOnlyList<string> Errors) Parse(string? json)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return (null, new[] { "Mapping produced no output — the closed contract requires an object with erpKey and erpStatus." });

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return (null, new[] { $"Mapping output is not valid JSON: {ex.Message}" }); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, new[] { $"Closed contract requires a JSON object at the root; got {root.ValueKind}." });

            foreach (var prop in root.EnumerateObject())
                if (!KnownKeys.Contains(prop.Name, StringComparer.Ordinal))
                    errors.Add($"Unknown key '{prop.Name}' — the response/ack contract is closed ({{ erpKey, erpStatus, message?, correlationBag? }}).");

            string? erpKey = null, erpStatus = null, message = null;
            JsonElement? correlationBag = null;

            if (!root.TryGetProperty("erpKey", out var keyEl) || keyEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(keyEl.GetString()))
                errors.Add("'erpKey' is required and must be a non-empty string.");
            else erpKey = keyEl.GetString();

            if (!root.TryGetProperty("erpStatus", out var statusEl) || statusEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(statusEl.GetString()))
                errors.Add("'erpStatus' is required and must be a non-empty string.");
            else erpStatus = statusEl.GetString();

            if (root.TryGetProperty("message", out var msgEl))
            {
                if (msgEl.ValueKind == JsonValueKind.String) message = msgEl.GetString();
                else if (msgEl.ValueKind != JsonValueKind.Null)
                    errors.Add($"'message' must be a string when present; got {msgEl.ValueKind}.");
            }

            if (root.TryGetProperty("correlationBag", out var bagEl))
            {
                if (bagEl.ValueKind == JsonValueKind.Object) correlationBag = bagEl.Clone();
                else if (bagEl.ValueKind != JsonValueKind.Null)
                    errors.Add($"'correlationBag' must be an object when present; got {bagEl.ValueKind}.");
            }

            return errors.Count > 0
                ? (null, errors)
                : (new LnOutboundAck(erpKey!, erpStatus!, message, correlationBag), errors);
        }
    }
}
