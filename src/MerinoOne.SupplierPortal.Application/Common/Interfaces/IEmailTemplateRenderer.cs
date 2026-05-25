namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Looks up an admin-editable email template by <c>TemplateKey</c> and substitutes
/// <c>{{placeholder}}</c> tokens from the supplied dictionary.
/// </summary>
public interface IEmailTemplateRenderer
{
    /// <summary>
    /// Look up the active template by key and substitute <c>{{placeholder}}</c> tokens from the
    /// supplied dictionary. Returns null when no ACTIVE row exists for the key — callers
    /// must fall back to a hardcoded body (existing behaviour). Unknown placeholders are left
    /// as literal <c>{{name}}</c> in the output (no exception).
    /// </summary>
    Task<RenderedEmail?> TryRenderAsync(
        string templateKey,
        IReadOnlyDictionary<string, string?> placeholders,
        CancellationToken ct = default);
}

/// <summary>Rendered subject (plain text) + html body returned by <see cref="IEmailTemplateRenderer"/>.</summary>
public sealed record RenderedEmail(string Subject, string HtmlBody);
