using System.Xml.Linq;
using MerinoOne.SupplierPortal.Application.Integration.Idm;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §5.5 / D-R8-21/22/23. Parses the IDM XML response (JSON in / XML out) and classifies
/// the outcome. Success = 2xx + <c>&lt;item&gt;</c> carrying <c>&lt;pid&gt;</c> (the …-LATEST mutation handle);
/// <c>&lt;pid2&gt;</c>/<c>&lt;id&gt;</c>/<c>&lt;version&gt;</c> captured. 4xx = permanent Validation (surface
/// <c>&lt;detail&gt;</c>); 5xx = Transient. A malformed 2xx (no pid) is treated as Validation — never a silent success.
/// Namespaced (<c>http://infor.com/daf</c>) and bare element names are both matched by local name.
/// </summary>
public sealed class IdmAckParser : IIdmAckParser
{
    public IdmAck Parse(int httpStatus, string xmlBody)
    {
        if (httpStatus >= 500)
            return new IdmAck(null, null, null, null, IdmFailureClass.Transient, ExtractDetailSafe(xmlBody) ?? $"HTTP {httpStatus}");

        if (httpStatus is < 200 or >= 300 && httpStatus < 500)
            return new IdmAck(null, null, null, null, IdmFailureClass.Validation, ExtractDetailSafe(xmlBody) ?? $"HTTP {httpStatus}");

        if (httpStatus is < 200 or >= 300)
            return new IdmAck(null, null, null, null, IdmFailureClass.Transient, $"Unexpected HTTP {httpStatus}");

        // 2xx — expect an <item> with a <pid>.
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlBody);
        }
        catch (System.Xml.XmlException ex)
        {
            return new IdmAck(null, null, null, null, IdmFailureClass.Validation, $"Malformed IDM XML: {ex.Message}");
        }

        // An error body can still arrive with a 2xx envelope — treat any <error>/<detail> as Validation.
        var errorDetail = ExtractDetail(doc);
        if (errorDetail is not null)
            return new IdmAck(null, null, null, null, IdmFailureClass.Validation, errorDetail);

        var pid = Element(doc, "pid");
        if (string.IsNullOrWhiteSpace(pid))
            return new IdmAck(null, null, null, null, IdmFailureClass.Validation, "IDM 2xx response carried no <pid>.");

        return new IdmAck(
            Pid: pid,
            Pid2: Element(doc, "pid2"),
            Id: Element(doc, "id"),
            Version: Element(doc, "version"),
            Failure: IdmFailureClass.None,
            Detail: null);
    }

    private static string? Element(XDocument doc, string localName)
        => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim() is { Length: > 0 } v ? v : null;

    private static string? ExtractDetail(XDocument doc)
    {
        var detail = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "detail")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(detail)) return detail;
        var error = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "error");
        if (error is null) return null;
        var msg = error.Value?.Trim();
        return string.IsNullOrWhiteSpace(msg) ? "IDM error" : msg;
    }

    private static string? ExtractDetailSafe(string xmlBody)
    {
        if (string.IsNullOrWhiteSpace(xmlBody)) return null;
        try { return ExtractDetail(XDocument.Parse(xmlBody)); }
        catch (System.Xml.XmlException) { return null; }
    }
}
