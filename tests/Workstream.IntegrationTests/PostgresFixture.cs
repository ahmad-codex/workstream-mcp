using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Workstream.Data;
using Workstream.Data.Migrations;
using Xunit;

namespace Workstream.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    public NpgsqlConnectionFactory ConnectionFactory { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("workstream_test")
            .WithUsername("workstream")
            .WithPassword("test_password")
            .Build();
        await _container.StartAsync().ConfigureAwait(false);

        ConnectionString = _container.GetConnectionString();
        ConnectionFactory = new NpgsqlConnectionFactory(
            Options.Create(new NpgsqlConnectionFactoryOptions { ConnectionString = ConnectionString }));

        var dir = ResolveMigrationsDirectory();
        var runner = new SqlMigrationRunner(ConnectionFactory, dir);
        await runner.ApplyAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await ConnectionFactory.DisposeAsync().ConfigureAwait(false);
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    private static string ResolveMigrationsDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "migrations");
        if (Directory.Exists(dir)) return dir;
        var s = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var c = Path.Combine(s, "deploy", "migrations");
            if (Directory.Exists(c)) return c;
            s = Path.GetDirectoryName(s) ?? throw new DirectoryNotFoundException();
        }
        throw new DirectoryNotFoundException("migrations not found");
    }
}
