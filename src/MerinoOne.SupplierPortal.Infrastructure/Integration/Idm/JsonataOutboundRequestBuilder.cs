using System.Text.Json;
using Jsonata.Net.Native;
using MerinoOne.SupplierPortal.Application.Integration.Idm;
using MerinoOne.SupplierPortal.Application.Integration.Ln;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §4 / D-R8-9. Pure JSONata evaluator: serialises the snapshot, runs the mapping
/// expression, and parses the resulting <c>{ headers, body }</c> envelope. JSONata does static values +
/// projection only (D-R8-18) — the file fetch happened at snapshot assembly, so <c>attachment.base64</c> is
/// already present. Also serves <see cref="IJsonataValidator"/> for the Save-config compile check.
/// R10 — evaluation routed through the shared engine (bounded compiled-expression cache) instead of
/// compiling a fresh JsonataQuery per call (R10 audit finding).
/// </summary>
public sealed class JsonataOutboundRequestBuilder : IOutboundRequestBuilder, IJsonataValidator
{
    private readonly ILnMappingService _mapping;
    public JsonataOutboundRequestBuilder(ILnMappingService mapping) => _mapping = mapping;

    public Task<OutboundEnvelope> BuildAsync(string mappingExpression, object snapshot, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(snapshot);
        var eval = _mapping.Evaluate(mappingExpression, inputJson);
        if (!eval.Ok || eval.OutputJson is null)
            throw new InvalidOperationException($"Mapping expression failed: {eval.Error ?? "no output"}");
        var outJson = eval.OutputJson;

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
