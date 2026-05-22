using System.Data.Common;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Opens raw ADO.NET connections for Dapper-driven read paths
/// (global search, dashboard, audit) that don't justify a full EF projection.
/// Implementation lives in Infrastructure and uses the same connection string as <see cref="IAppDbContext"/>.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>Returns an OPEN <see cref="DbConnection"/>. Caller disposes.</summary>
    Task<DbConnection> OpenAsync(CancellationToken ct = default);
}
