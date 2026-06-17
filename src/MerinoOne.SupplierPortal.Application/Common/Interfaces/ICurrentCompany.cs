using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Per-request active-company context for the always-on company filter (parallel to <see cref="ICurrentUser"/>
/// and Seccode RLS). The active company comes from the <c>X-Active-Company</c> header, validated to be in the
/// caller's accessible set. The sharing resolver normalizes a member company to its source company per endpoint.
/// </summary>
public interface ICurrentCompany
{
    /// <summary>The single active company (TenantEntityId). Basis for the plain company filter on business data.</summary>
    Guid? ActiveCompanyId { get; }

    /// <summary>The companies this principal may select between. Tenant Admin → all active companies in tenant.</summary>
    IReadOnlyCollection<Guid> AccessibleCompanyIds { get; }

    /// <summary>
    /// True when the active company is one this user holds an <c>AllSuppliers=true</c> UserCompanyMap for —
    /// a direct full-company grant. Drives a seccode bypass that is SCOPED to the active company only (the
    /// company filter already ANDs <c>TenantEntityId == ActiveCompanyId</c>, so there is no cross-company
    /// leak). Read from the DB per request, never from a JWT claim (avoids stale-token privilege escalation).
    /// Admins are already privileged (bypass seccode via SecCurrentUserIsPrivileged), so this returns false
    /// / is irrelevant for them.
    /// </summary>
    bool ActiveCompanyFullAccess { get; }

    /// <summary>
    /// Sharing-aware normalization for the two master endpoints. Returns the group source for a member
    /// company; the company itself when it belongs to no group; null when <paramref name="companyId"/> is null.
    /// Memoized per request. Reused by the inbound write path to normalize incoming company → source.
    /// </summary>
    Guid? ResolveSource(SharedEndpoint endpoint, Guid? companyId);
}
