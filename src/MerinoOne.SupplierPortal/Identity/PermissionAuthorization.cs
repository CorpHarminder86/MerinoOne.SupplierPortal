using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace MerinoOne.SupplierPortal.Identity;

/// <summary>
/// Requires the authenticated user to carry a "permission" claim equal to the policy name.
/// Lets us write <c>[Authorize(Policy = "PurchaseOrder.Read")]</c> without enumerating every
/// permission upfront; PermissionPolicyProvider lazily materialises the policy on first use.
/// </summary>
public class PermissionRequirement : IAuthorizationRequirement
{
    public string PermissionCode { get; }
    public PermissionRequirement(string code) => PermissionCode = code;
}

public class PermissionRequirementHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim("permission", requirement.PermissionCode))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var existing = await base.GetPolicyAsync(policyName);
        if (existing != null) return existing;

        // Treat any policy that looks like a permission code (contains a dot) as a permission claim check.
        if (!string.IsNullOrEmpty(policyName) && policyName.Contains('.'))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName))
                .Build();
        }
        return null;
    }
}
