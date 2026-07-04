using System.Text.Json;
using System.Text.Json.Nodes;
using MerinoOne.SupplierPortal.Application.Integration.Idm;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §4.3. Evaluates the configured JSON array of required-non-null dot-paths against the
/// snapshot. A row is eligible only when EVERY path resolves to a non-null, non-empty value. One mechanism for
/// every attachment type — the gate is config, not code (D-R8-17). A malformed gate JSON fails closed (unsatisfied).
/// </summary>
public sealed class JsonPathEligibilityGate : IEligibilityGate
{
    public bool IsSatisfied(string eligibilityGateJson, object snapshot)
    {
        if (string.IsNullOrWhiteSpace(eligibilityGateJson)) return false;

        string[] paths;
        try
        {
            paths = JsonSerializer.Deserialize<string[]>(eligibilityGateJson) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return false; // malformed gate → fail closed
        }
        if (paths.Length == 0) return false;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(JsonSerializer.Serialize(snapshot));
        }
        catch (JsonException)
        {
            return false;
        }
        if (root is null) return false;

        foreach (var path in paths)
        {
            var node = Resolve(root, path);
            if (node is null) return false;
            if (node is JsonValue v)
            {
                var s = v.ToString();
                if (string.IsNullOrWhiteSpace(s)) return false;
            }
        }
        return true;
    }

    private static JsonNode? Resolve(JsonNode root, string dottedPath)
    {
        JsonNode? current = root;
        foreach (var segment in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current) || current is null)
                return null;
        }
        return current;
    }
}
