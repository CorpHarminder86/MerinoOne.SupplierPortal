using System.Net;
using System.Text.RegularExpressions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// Loads the active <c>EmailTemplate</c> row for a key (60-second IMemoryCache),
/// then substitutes <c>{{placeholder}}</c> tokens from the supplied dictionary.
/// Body values are HTML-encoded on substitution; subject values are inserted verbatim
/// (subject is plain text, so encoding would garble it).
/// </summary>
internal sealed class EmailTemplateRenderer : IEmailTemplateRenderer
{
    /// <summary>Memory-cache key prefix; admin update handlers <c>Remove</c> by this prefix on save.</summary>
    public const string CacheKeyPrefix = "emailtpl:";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private static readonly Regex PlaceholderRegex = new(
        @"\{\{\s*([A-Za-z0-9_]+)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IAppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EmailTemplateRenderer> _logger;

    public EmailTemplateRenderer(
        IAppDbContext db,
        IMemoryCache cache,
        ILogger<EmailTemplateRenderer> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<RenderedEmail?> TryRenderAsync(
        string templateKey,
        IReadOnlyDictionary<string, string?> placeholders,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
            return null;

        var template = await LoadAsync(templateKey, ct);
        if (template is null)
        {
            _logger.LogInformation(
                "No active template for key {Key}, falling back to hardcoded body.",
                templateKey);
            return null;
        }

        var subject = Substitute(template.Subject, placeholders, htmlEncode: false);
        var htmlBody = Substitute(template.HtmlBody, placeholders, htmlEncode: true);

        _logger.LogDebug("Rendered template {Key} (subject {SubjectLen} chars, body {BodyLen} chars).",
            templateKey, subject.Length, htmlBody.Length);

        return new RenderedEmail(subject, htmlBody);
    }

    private async Task<CachedTemplate?> LoadAsync(string key, CancellationToken ct)
    {
        var cacheKey = CacheKeyPrefix + key;
        if (_cache.TryGetValue(cacheKey, out CachedTemplate? cached))
            return cached;

        // IgnoreQueryFilters keeps us aligned with the seeder (the soft-delete filter would
        // hide tombstoned rows automatically anyway — we add !IsDeleted explicitly to be safe).
        var row = await _db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TemplateKey == key && t.IsActive && !t.IsDeleted)
            .Select(t => new CachedTemplate(t.Subject, t.HtmlBody))
            .FirstOrDefaultAsync(ct);

        // Cache positive AND negative hits so a missing/inactive template doesn't hammer the DB
        // every send. Same TTL — 60s is short enough that toggling IsActive is felt quickly.
        _cache.Set(cacheKey, row, CacheTtl);
        return row;
    }

    private static string Substitute(
        string template,
        IReadOnlyDictionary<string, string?> placeholders,
        bool htmlEncode)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;

        return PlaceholderRegex.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (!placeholders.TryGetValue(name, out var raw))
            {
                // Unknown placeholder — leave the literal `{{name}}` in place.
                return match.Value;
            }
            var value = raw ?? string.Empty;
            return htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });
    }

    private sealed record CachedTemplate(string Subject, string HtmlBody);
}
