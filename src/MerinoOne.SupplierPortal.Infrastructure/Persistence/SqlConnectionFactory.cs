using System.Data.Common;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.Data.SqlClient;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence;

/// <summary>
/// Returns an OPEN <see cref="SqlConnection"/> built from <c>ConnectionStrings:DefaultConnection</c>.
/// Used by Dapper-backed read paths (global search, audit trail) where a hand-tuned UNION ALL
/// is materially faster than EF projection.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString) => _connectionString = connectionString;

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(ct);
        return cn;
    }
}
