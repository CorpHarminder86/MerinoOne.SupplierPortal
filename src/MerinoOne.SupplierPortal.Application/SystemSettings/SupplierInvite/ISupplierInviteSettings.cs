namespace MerinoOne.SupplierPortal.Application.SystemSettings.SupplierInvite;

/// <summary>Strongly-typed reader for the SupplierInvite category.</summary>
public interface ISupplierInviteSettings
{
    /// <summary>Days before a supplier-invite token expires. Defaults to 7 when unset/invalid.</summary>
    int ExpiryDays { get; }
}
