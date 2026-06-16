using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Domain.Enums;

namespace MerinoOne.SupplierPortal.Infrastructure.Identity;

/// <summary>
/// System / design-time <see cref="ICurrentCompany"/> used by ef tooling, seeders and background
/// workers. Implements <see cref="ISystemCompany"/> so the company filter is bypassed. ResolveSource
/// returns the company unchanged (no sharing) — system writers normalize explicitly when needed.
/// </summary>
public class AnonymousCurrentCompany : ICurrentCompany, ISystemCompany
{
    public Guid? ActiveCompanyId => null;
    public IReadOnlyCollection<Guid> AccessibleCompanyIds { get; } = Array.Empty<Guid>();
    public Guid? ResolveSource(SharedEndpoint endpoint, Guid? companyId) => companyId;
}
