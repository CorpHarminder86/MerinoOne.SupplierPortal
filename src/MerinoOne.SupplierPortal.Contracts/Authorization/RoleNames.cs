namespace MerinoOne.SupplierPortal.Contracts.Authorization;

/// <summary>
/// The built-in role names. Use these constants instead of raw string literals in role-name checks
/// (<c>Roles.Contains(RoleNames.Admin)</c>) on both client and server. The seeded role set is built
/// from <see cref="BuiltIn"/>.
/// </summary>
public static class RoleNames
{
    public const string PlatformAdmin = "PlatformAdmin";
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Buyer = "Buyer";
    public const string Finance = "Finance";
    public const string Supplier = "Supplier";
    public const string ReadOnly = "ReadOnly";

    /// <summary>All built-in role names, in privilege order.</summary>
    public static readonly string[] BuiltIn =
        { PlatformAdmin, SuperAdmin, Admin, Buyer, Finance, Supplier, ReadOnly };
}
