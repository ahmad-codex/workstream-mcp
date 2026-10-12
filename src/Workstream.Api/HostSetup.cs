using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Api.Mcp;
using Workstream.Core.StateMachine;
using Workstream.Data;
using Workstream.Data.Repositories;
using Workstream.Mcp.Notifications;

namespace Workstream.Api;

/// <summary>Registration and configuration shared by the HTTP host and the stdio server.</summary>
public static class HostSetup
{
    /// <summary>Data layer, state machine and outbox enqueue adapters.</summary>
    public static IServiceCollection AddWorkstreamData(this IServiceCollection services, string connectionString)
    {
        services.Configure<NpgsqlConnectionFactoryOptions>(o => o.ConnectionString = connectionString);

        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IProjectRepository, ProjectRepository>();
        services.AddSingleton<IPlanRepository, PlanRepository>();
        services.AddSingleton<IPlanTypeRepository, PlanTypeRepository>();
        services.AddSingleton<ITaskRepository, TaskRepository>();
        services.AddSingleton<IFindingRepository, FindingRepository>();
        services.AddSingleton<IAttemptRepository, AttemptRepository>();
        services.AddSingleton<IVerdictRepository, VerdictRepository>();
        services.AddSingleton<IEventRepository, EventRepository>();
        services.AddSingleton<IOutboxRepository, OutboxRepository>();

        services.AddSingleton<IPlanTypeCache, PostgresPlanTypeCache>();
        services.AddSingleton<StateMachineService>();

        services.AddSingleton<IBoardSyncEnqueue, OutboxBoardSyncEnqueue>();
        services.AddSingleton<ISlackNotifyEnqueue, OutboxSlackNotifyEnqueue>();
        return services;
    }

    /// <summary>
    /// Resolves the Postgres connection string. If WORKSTREAM_DB_CONNECTION contains the
    /// __from_secret__ placeholder (the pattern from §12.1), substitute it with the contents
    /// of WORKSTREAM_DB_PASSWORD_FILE. Returns null when unset and <paramref name="required"/> is false.
    /// </summary>
    public static string? BuildDbConnectionString(IConfiguration cfg, bool required = true)
    {
        var conn = cfg["WORKSTREAM_DB_CONNECTION"] ?? Environment.GetEnvironmentVariable("WORKSTREAM_DB_CONNECTION") ?? "";
        if (string.IsNullOrEmpty(conn))
        {
            if (!required) return null;
            throw new InvalidOperationException("WORKSTREAM_DB_CONNECTION must be set");
        }
        if (conn.Contains("__from_secret__", StringComparison.Ordinal))
        {
            var pwFile = cfg["WORKSTREAM_DB_PASSWORD_FILE"] ?? Environment.GetEnvironmentVariable("WORKSTREAM_DB_PASSWORD_FILE");
            if (string.IsNullOrEmpty(pwFile) || !File.Exists(pwFile))
                throw new InvalidOperationException("__from_secret__ placeholder requires WORKSTREAM_DB_PASSWORD_FILE pointing at a readable file");
            var pw = File.ReadAllText(pwFile).Trim();
            conn = conn.Replace("__from_secret__", pw, StringComparison.Ordinal);
        }
        return conn;
    }

    public static string ResolveMigrationsDir()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "migrations"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "migrations"),
            Path.Combine(Directory.GetCurrentDirectory(), "deploy", "migrations"),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return Path.GetFullPath(c);
        }
        return Path.GetFullPath(candidates[^1]);
    }
}
