using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Workstream.Data.Migrations;

/// <summary>
/// Applies <c>deploy/migrations/*.sql</c> in numeric order. Each migration runs in a single
/// transaction. The <c>__migrations</c> table (created by the first file) tracks applied versions.
/// Idempotent: re-running is a no-op when the version row already exists.
/// </summary>
public sealed class SqlMigrationRunner
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<SqlMigrationRunner>? _log;
    private readonly string _migrationsDir;

    public SqlMigrationRunner(
        IDbConnectionFactory factory,
        string migrationsDir,
        ILogger<SqlMigrationRunner>? log = null)
    {
        _factory = factory;
        _migrationsDir = migrationsDir;
        _log = log;
    }

    public async Task ApplyAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_migrationsDir))
            throw new InvalidOperationException($"migrations directory not found: {_migrationsDir}");

        var files = Directory.EnumerateFiles(_migrationsDir, "*.sql")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        // Bootstrap the bookkeeping table if it doesn't exist yet — the first migration
        // creates it inside its own transaction, but we need it before running 0002+.
        await EnsureBookkeepingExistsAsync(conn, ct).ConfigureAwait(false);

        foreach (var file in files)
        {
            var version = Path.GetFileNameWithoutExtension(file);
            if (await IsAppliedAsync(conn, version, ct).ConfigureAwait(false))
            {
                _log?.LogDebug("migration {Version} already applied", version);
                continue;
            }

            _log?.LogInformation("applying migration {Version}", version);
            var sql = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var checksum = ComputeChecksum(sql);

            // Each migration file controls its own BEGIN/COMMIT — we just execute it.
            await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct, commandTimeout: 300)).ConfigureAwait(false);

            // The migration file itself inserts into __migrations, but we update the checksum here
            // for forensic comparison.
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE __migrations SET checksum = @checksum WHERE version = @version",
                new { checksum, version }, cancellationToken: ct)).ConfigureAwait(false);

            _log?.LogInformation("applied migration {Version} (checksum {Checksum})", version, checksum);
        }
    }

    private static async Task EnsureBookkeepingExistsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE IF NOT EXISTS __migrations (
                version    text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now(),
                checksum   text
            )
            """, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task<bool> IsAppliedAsync(NpgsqlConnection conn, string version, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT version FROM __migrations WHERE version = @version", new { version }, cancellationToken: ct))
            .ConfigureAwait(false);
        return row is not null;
    }

    private static string ComputeChecksum(string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(sql);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
