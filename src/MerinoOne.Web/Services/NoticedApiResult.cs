using MerinoOne.SupplierPortal.Contracts.Common;

namespace MerinoOne.Web.Services;

/// <summary>
/// R6 (2026-07-02) — Web-side extension of the Contracts <see cref="ApiResult{T}"/> envelope.
///
/// The API's Application-layer <c>Result&lt;T&gt;</c> gained a <c>Notices</c> array (advisory lines on a
/// SUCCESSFUL response — e.g. invoice-submit tax-rate drift: "Tax GST18: rate changed 18% → 12%"). The
/// Contracts mirror does not carry it, and this project consumes Contracts read-only, so the Web deserialises
/// submit-style responses into this derived envelope instead — System.Text.Json binds the base properties as
/// usual and picks up <c>notices</c> here. Empty when there is nothing to say.
///
/// <see cref="StatusCode"/> is NOT a serialized API property: it is stamped by
/// <see cref="ApiClient.PostWithNoticesAsync{T,TBody}"/> from the folded <see cref="ApiException"/> on a
/// non-2xx response (null on success), so pages can branch on specific failures (e.g. the invoice-submit
/// 409 reservation conflict) without changing the established errors-folding convention.
/// </summary>
public class NoticedApiResult<T> : ApiResult<T>
{
    public List<string> Notices { get; init; } = new();

    /// <summary>HTTP status of a folded non-2xx response; null when the request reached a 2xx envelope.</summary>
    public int? StatusCode { get; init; }
}
