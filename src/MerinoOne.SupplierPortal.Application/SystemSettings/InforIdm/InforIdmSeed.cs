namespace MerinoOne.SupplierPortal.Application.SystemSettings.InforIdm;

/// <summary>
/// R8 (2026-07-04) — declares the InforIdm settings category (dispatcher knobs). Registered as
/// <c>ISettingsCategorySeed</c> so the generic Get/Save/Reset pipeline picks it up; the typed reader is
/// <see cref="InforIdmSettingsService"/>.
/// </summary>
public class InforIdmSeed : ISettingsCategorySeed
{
    public string Category => InforIdmKeys.Category;

    public IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [InforIdmKeys.DispatcherPollSeconds] = "10",
        [InforIdmKeys.BatchSize] = "25",
        [InforIdmKeys.ConcurrencyCap] = "4",
        [InforIdmKeys.RetryBackoffBaseSeconds] = "30",
        [InforIdmKeys.RetryBackoffCapSeconds] = "3600",
        [InforIdmKeys.MaxAttempts] = "8",
        [InforIdmKeys.StaleInFlightMinutes] = "5",
    };

    public IReadOnlyDictionary<string, string?> Descriptions { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [InforIdmKeys.DispatcherPollSeconds] = "How often the IDM dispatcher drains the outbox (seconds). Range 5–300.",
        [InforIdmKeys.BatchSize] = "Rows claimed per drain pass. Range 1–500.",
        [InforIdmKeys.ConcurrencyCap] = "Max concurrent in-flight dispatches (each holds a base64-encoded file in memory). Range 1–16.",
        [InforIdmKeys.RetryBackoffBaseSeconds] = "Exponential backoff base for transient failures (seconds). Range 5–600.",
        [InforIdmKeys.RetryBackoffCapSeconds] = "Maximum backoff delay (seconds). Must be ≥ the base.",
        [InforIdmKeys.MaxAttempts] = "Transient-failure attempts before a row becomes terminal Failed. Range 1–20.",
        [InforIdmKeys.StaleInFlightMinutes] = "Recover an InFlight row stranded by a crash after this many minutes. Range 1–120.",
    };

    public string? Validate(string key, string value)
    {
        switch (key)
        {
            case InforIdmKeys.DispatcherPollSeconds: return Range(value, 5, 300);
            case InforIdmKeys.BatchSize: return Range(value, 1, 500);
            case InforIdmKeys.ConcurrencyCap: return Range(value, 1, 16);
            case InforIdmKeys.RetryBackoffBaseSeconds: return Range(value, 5, 600);
            case InforIdmKeys.RetryBackoffCapSeconds: return Range(value, 5, 86_400);
            case InforIdmKeys.MaxAttempts: return Range(value, 1, 20);
            case InforIdmKeys.StaleInFlightMinutes: return Range(value, 1, 120);
            default: return $"Unknown InforIdm key '{key}'.";
        }
    }

    private static string? Range(string value, int min, int max) =>
        int.TryParse(value, out var n) && n >= min && n <= max
            ? null
            : $"Value must be an integer between {min} and {max}.";
}
