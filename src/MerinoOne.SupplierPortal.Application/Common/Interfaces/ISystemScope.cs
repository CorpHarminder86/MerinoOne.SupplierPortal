namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Marker for a system / background / design-time <see cref="ICurrentUser"/> that bypasses BOTH the
/// tenant and company filters (workers, seeders, migrations, outbound integration, EF tooling).
/// Such principals set scope explicitly on the rows they write, so the ScopeStampInterceptor skips them.
/// </summary>
public interface ISystemPrincipal { }

/// <summary>
/// Marker for a system <see cref="ICurrentCompany"/> that bypasses the company filter. Pairs with
/// <see cref="ISystemPrincipal"/> so background operations see all companies within a (system) request scope.
/// </summary>
public interface ISystemCompany { }
