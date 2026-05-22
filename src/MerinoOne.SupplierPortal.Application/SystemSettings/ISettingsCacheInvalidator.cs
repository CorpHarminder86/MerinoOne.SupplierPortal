namespace MerinoOne.SupplierPortal.Application.SystemSettings;

/// <summary>
/// A cached settings consumer that can be told to drop its snapshot for a given category.
/// Implementations (e.g. <c>EmailConfigService</c>, <c>SupplierInviteSettingsService</c>) are
/// registered as singletons; Save/Reset handlers iterate every registered invalidator after
/// committing so the next read picks up the fresh value.
/// </summary>
public interface ISettingsCacheInvalidator
{
    void InvalidateCategory(string category);
}
