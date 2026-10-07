using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Workstream.Data;
using Workstream.Data.Migrations;
using Xunit;

namespace Workstream.ConcurrencyTests;

/// <summary>
/// Spins up a throwaway Postgres 16 container, applies <c>deploy/migrations/*.sql</c>,
/// exposes a connection factory. One instance shared across the test class (IClassFixture);
/// each test seeds and tears down its own data.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    public NpgsqlConnectionFactory ConnectionFactory { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("workstream_test")
            .WithUsername("workstream")
            .WithPassword("test_password")
            .Build();
        await _container.StartAsync().ConfigureAwait(false);

        ConnectionString = _container.GetConnectionString();
        ConnectionFactory = new NpgsqlConnectionFactory(
            Options.Create(new NpgsqlConnectionFactoryOptions { ConnectionString = ConnectionString }));

        var migrationsDir = ResolveMigrationsDirectory();
        var runner = new SqlMigrationRunner(ConnectionFactory, migrationsDir);
        await runner.ApplyAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await ConnectionFactory.DisposeAsync().ConfigureAwait(false);
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    private static string ResolveMigrationsDirectory()
    {
        // The csproj links deploy/migrations/*.sql into bin/.../migrations
        var dir = Path.Combine(AppContext.BaseDirectory, "migrations");
        if (Directory.Exists(dir)) return dir;

        // Fallback: walk up to the repo root in case CopyToOutputDirectory hasn't run.
        var search = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(search, "deploy", "migrations");
            if (Directory.Exists(candidate)) return candidate;
            search = Path.GetDirectoryName(search) ?? throw new DirectoryNotFoundException("migrations directory not found");
        }
        throw new DirectoryNotFoundException($"migrations directory not found from {AppContext.BaseDirectory}");
    }
}
