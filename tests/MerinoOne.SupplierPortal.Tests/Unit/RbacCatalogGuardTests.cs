using System.Reflection;
using FluentValidation;
using FluentAssertions;
using MerinoOne.SupplierPortal.Contracts.Authorization;
using MerinoOne.SupplierPortal.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace MerinoOne.SupplierPortal.Tests.Unit;

/// <summary>
/// Pure reflection/string guards (no DB) that lock the RBAC single-source-of-truth invariants and would
/// have caught the historical RoleDetail.razor drift (a stale code that broke Save) at build time.
/// </summary>
public class RbacCatalogGuardTests
{
    private static readonly HashSet<string> CatalogCodes =
        PermissionCatalog.All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void PermConstants_and_PermissionCatalog_are_the_same_set()
    {
        // The Perm.* constants (used by [Authorize] + HasPermission) and the seeded catalog must match
        // exactly — no orphan constant, no un-constanted seeded code.
        Perm.All.Should().BeEquivalentTo(CatalogCodes);
    }

    [Fact]
    public void Every_permission_in_the_matrix_exists_in_the_catalog()
    {
        var missing = PermissionCatalog.Matrix.Keys.Where(k => !CatalogCodes.Contains(k)).ToArray();
        missing.Should().BeEmpty("the role→permission matrix must only reference seeded permission codes");
    }

    [Fact]
    public void Every_matrix_role_is_a_known_built_in_role()
    {
        var roles = RoleNames.BuiltIn.ToHashSet(StringComparer.Ordinal);
        var unknown = PermissionCatalog.Matrix.Values.SelectMany(v => v).Distinct()
            .Where(r => !roles.Contains(r)).ToArray();
        unknown.Should().BeEmpty("the matrix must only grant to built-in role names");
        PermissionCatalog.Roles.Should().BeEquivalentTo(RoleNames.BuiltIn);
    }

    [Fact]
    public void Every_Authorize_policy_code_exists_in_the_catalog()
    {
        // Reflect over every controller in the API assembly; any [Authorize(Policy="X.Y")] whose policy
        // looks like a permission code (contains a dot) MUST be a seeded permission — else the lazy policy
        // provider builds a claim check nobody can ever satisfy (silent fail-closed) or, if ticked in a UI,
        // an "Unknown permissions" hard error on save.
        var apiAssembly = typeof(MerinoOne.SupplierPortal.Controllers.RolesController).Assembly;

        var policyCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in apiAssembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t)))
        {
            CollectPolicies(type.GetCustomAttributes<AuthorizeAttribute>(inherit: true), policyCodes);
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                CollectPolicies(m.GetCustomAttributes<AuthorizeAttribute>(inherit: true), policyCodes);
        }

        // Sanity: reflection actually found policy-gated endpoints.
        policyCodes.Should().NotBeEmpty();

        var unknown = policyCodes.Where(c => !CatalogCodes.Contains(c)).OrderBy(c => c).ToArray();
        unknown.Should().BeEmpty("every [Authorize(Policy=...)] permission code must be seeded in PermissionCatalog.All");

        static void CollectPolicies(IEnumerable<AuthorizeAttribute> attrs, HashSet<string> sink)
        {
            foreach (var a in attrs)
                if (!string.IsNullOrEmpty(a.Policy) && a.Policy.Contains('.'))
                    sink.Add(a.Policy);
        }
    }

    [Fact]
    public void RoleDetail_razor_has_no_hardcoded_permission_catalog()
    {
        // The picker must be API-driven (GET api/permissions). A reintroduced hardcoded PermDef[] would drift.
        var razor = FindRepoFile("src/MerinoOne.Web/Components/Pages/Admin/RoleDetail.razor");
        var text = File.ReadAllText(razor);
        text.Should().NotContain("new PermDef(", "the role picker must load the catalog from the API, not a hardcoded list");
        text.Should().Contain("api/permissions", "the role picker must fetch the assignable-permission catalog from the backend");
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate '{relative}' walking up from {AppContext.BaseDirectory}");
    }
}
