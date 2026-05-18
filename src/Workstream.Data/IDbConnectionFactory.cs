using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Workstream.Data;

/// <summary>
/// Hands out fresh Npgsql connections. The factory owns the connection-string and
/// (optionally) data-source-level config; callers own the lifetime of the connection.
/// </summary>
public interface IDbConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default);
}
