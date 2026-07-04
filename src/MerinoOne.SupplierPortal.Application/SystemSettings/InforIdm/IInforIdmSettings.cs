namespace MerinoOne.SupplierPortal.Application.SystemSettings.InforIdm;

/// <summary>R8 (2026-07-04) — strongly-typed reader for the InforIdm dispatcher settings category.</summary>
public interface IInforIdmSettings
{
    int DispatcherPollSeconds { get; }
    int BatchSize { get; }
    int ConcurrencyCap { get; }
    int RetryBackoffBaseSeconds { get; }
    int RetryBackoffCapSeconds { get; }
    int MaxAttempts { get; }
    int StaleInFlightMinutes { get; }
}
