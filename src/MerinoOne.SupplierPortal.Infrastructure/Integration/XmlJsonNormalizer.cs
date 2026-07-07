using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration;

/// <summary>
/// R10 — generic, deterministic XML→JSON normalization for outbound-integration response bodies
/// (<c>OutboundIntegrationConfig.ResponseFormat = Xml</c>). The mapping layer is JSONata-over-JSON only;
/// XML-speaking targets (IDM today, classic Tally later) declare their format and this converter runs
/// BEFORE the response mapping expression, so expressions never see wire XML.
///
/// <para>Rules (stable — expressions depend on them): element → property named by its LOCAL name
/// (namespaces dropped: the IDM daf namespace should not leak into expressions); repeated sibling names →
/// array; attributes → <c>"@name"</c> properties; an element with both attributes/children AND text keeps
/// the text under <c>"#text"</c>; a pure-text leaf becomes a plain string. The ROOT element becomes a
/// single-property object (<c>{"item": …}</c>) so expressions can distinguish <c>item</c> vs <c>error</c>.</para>
/// </summary>
public static class XmlJsonNormalizer
{
    /// <summary>Converts an XML document to its canonical JSON form, or null when the body is not well-formed XML.</summary>
    public static string? TryToJson(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XElement root;
        try { root = XElement.Parse(xml, LoadOptions.None); }
        catch { return null; }

        var obj = new JsonObject { [root.Name.LocalName] = Convert(root) };
        return obj.ToJsonString();
    }

    private static JsonNode? Convert(XElement element)
    {
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        var children = element.Elements().ToList();
        var text = element.Nodes().OfType<XText>().Select(t => t.Value).ToArray() is { Length: > 0 } parts
            ? string.Concat(parts).Trim() : string.Empty;

        // Pure text leaf → plain string (the dominant IDM case: <pid>MDS-…-LATEST</pid>).
        if (attributes.Count == 0 && children.Count == 0)
            return JsonValue.Create(text);

        var obj = new JsonObject();
        foreach (var attr in attributes)
            obj["@" + attr.Name.LocalName] = JsonValue.Create(attr.Value);

        foreach (var group in children.GroupBy(c => c.Name.LocalName))
        {
            var items = group.ToList();
            obj[group.Key] = items.Count == 1
                ? Convert(items[0])
                : new JsonArray(items.Select(Convert).ToArray());
        }

        if (text.Length > 0) obj["#text"] = JsonValue.Create(text);
        return obj;
    }
}
