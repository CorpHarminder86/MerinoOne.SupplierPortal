using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln;

/// <summary>
/// R9 — the ONE serializer boundary for the dynamic LN path.
///
/// <para><b>Input documents</b> (<see cref="InputDocOptions"/>): nulls are KEPT (unlike the legacy
/// WhenWritingNull payloads) so gates and the editor picker see every contract field; property names come
/// from the <c>[JsonPropertyName]</c> attributes on the frozen DTOs.</para>
///
/// <para><b>Canonical form</b> (<see cref="CanonicalWrite"/>): the bytes the dynamic transport POSTs and the
/// byte-parity harness compares. Re-serializing through one writer neutralises STJ-vs-Jsonata escaping
/// differences, and numbers are normalised through <see cref="decimal"/> with trailing zeros stripped
/// (<c>123.40</c> → <c>123.4</c>) because JSONata's number type does not preserve decimal scale. Applied
/// identically to the legacy-builder side and the JSONata side, so parity is byte-exact by construction;
/// on the wire LN parses OData numbers numerically, so scale normalisation is value-preserving.</para>
/// </summary>
internal static class LnJson
{
    /// <summary>Input-document serializer: keep nulls, no indentation, DTO-attribute property names.</summary>
    internal static readonly JsonSerializerOptions InputDocOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>Serialize an input document (nulls kept).</summary>
    internal static string SerializeInputDoc(object doc) => JsonSerializer.Serialize(doc, InputDocOptions);

    /// <summary>Canonicalise a JSON text: one escaping policy, normalised number scale, no whitespace.</summary>
    internal static string CanonicalWrite(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(doc.RootElement, writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(JsonElement el, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in el.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    WriteCanonical(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteCanonical(item, w);
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                w.WriteStringValue(el.GetString());
                break;
            case JsonValueKind.Number:
                if (el.TryGetDecimal(out var d))
                    // Strip trailing zeros (the max-scale division trick normalises 123.40m → 123.4m) so
                    // STJ decimal scale and JSONata's scale-less numbers canonicalise identically.
                    w.WriteNumberValue(d / 1.000000000000000000000000000000000m);
                else
                    w.WriteRawValue(el.GetRawText()); // outside decimal range — pass through verbatim
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                w.WriteBooleanValue(el.GetBoolean());
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }
}
