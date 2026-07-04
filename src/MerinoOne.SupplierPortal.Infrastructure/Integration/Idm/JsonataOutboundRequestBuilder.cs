using System.Text.Json;
using Jsonata.Net.Native;
using Jsonata.Net.Native.Json;
using MerinoOne.SupplierPortal.Application.Integration.Idm;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §4 / D-R8-9. Pure JSONata evaluator: serialises the snapshot, runs the mapping
/// expression, and parses the resulting <c>{ headers, body }</c> envelope. JSONata does static values +
/// projection only (D-R8-18) — the file fetch happened at snapshot assembly, so <c>attachment.base64</c> is
/// already present. Also serves <see cref="IJsonataValidator"/> for the Save-config compile check.
/// </summary>
public sealed class JsonataOutboundRequestBuilder : IOutboundRequestBuilder, IJsonataValidator
{
    public Task<OutboundEnvelope> BuildAsync(string mappingExpression, object snapshot, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(snapshot);
        var query = new JsonataQuery(mappingExpression);
        var result = query.Eval(JToken.Parse(inputJson));
        var outJson = result.ToFlatString();

        using var doc = JsonDocument.Parse(outJson);
        var root = doc.RootElement;

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in h.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Null) continue;
                headers[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            }
        }

        var body = root.TryGetProperty("body", out var b) ? b.GetRawText() : "{}";
        return Task.FromResult(new OutboundEnvelope(headers, body));
    }

    public string? Validate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "Expression is empty.";
        try { _ = new JsonataQuery(expression); return null; }
        catch (Exception ex) { return ex.Message; }
    }
}
