using MerinoOne.SupplierPortal.Application.Integration.Idm;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>R8 (2026-07-04) — resolves the snapshot provider for an IDM entity type (DI-aggregated).</summary>
public sealed class SnapshotProviderRegistry : ISnapshotProviderRegistry
{
    private readonly Dictionary<string, IEntitySnapshotProvider> _byType;

    public SnapshotProviderRegistry(IEnumerable<IEntitySnapshotProvider> providers)
    {
        _byType = providers.ToDictionary(p => p.IdmEntityType, StringComparer.Ordinal);
    }

    public IEntitySnapshotProvider? TryGet(string idmEntityType)
        => _byType.TryGetValue(idmEntityType, out var p) ? p : null;

    public IReadOnlyCollection<IEntitySnapshotProvider> All => _byType.Values;
}
