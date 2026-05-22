namespace MerinoOne.SupplierPortal.Application.SystemSettings.Registry;

/// <summary>
/// Lookup of every <see cref="ISettingsCategorySeed"/> registered in DI, keyed by category.
/// Singleton — populated once at startup; readers are lock-free.
/// </summary>
public class SettingsSeedRegistry
{
    private readonly IReadOnlyDictionary<string, ISettingsCategorySeed> _seeds;

    public SettingsSeedRegistry(IEnumerable<ISettingsCategorySeed> seeds)
    {
        _seeds = seeds.ToDictionary(s => s.Category, StringComparer.Ordinal);
    }

    /// <summary>Returns the seed for <paramref name="category"/> if one is registered.</summary>
    public bool TryGet(string category, out ISettingsCategorySeed? seed)
    {
        if (_seeds.TryGetValue(category, out var s))
        {
            seed = s;
            return true;
        }
        seed = null;
        return false;
    }

    public IEnumerable<ISettingsCategorySeed> All() => _seeds.Values;
}
