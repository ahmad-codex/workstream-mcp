using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Workstream.Data;

public sealed class NpgsqlConnectionFactoryOptions
{
    /// <summary>Postgres connection string. Resolved at app startup from env / secrets file.</summary>
    public string ConnectionString { get; set; } = "";
}

public sealed class NpgsqlConnectionFactory : IDbConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _ds;

    public NpgsqlConnectionFactory(IOptions<NpgsqlConnectionFactoryOptions> options)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.ConnectionString))
            throw new InvalidOperationException("WORKSTREAM_DB_CONNECTION must be configured");

        var builder = new NpgsqlDataSourceBuilder(opts.ConnectionString);
        // Per §11.2: OTel propagation comes from Npgsql.OpenTelemetry registration upstream.
        _ds = builder.Build();
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        return await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _ds.DisposeAsync();
}
