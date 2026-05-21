using System;
using System.IO;
using Microsoft.Extensions.Options;
using Workstream.Api.Endpoints;
using Workstream.Api.Mcp;
using Workstream.Api.Middleware;
using Workstream.Core.StateMachine;
using Workstream.Data;
using Workstream.Data.Migrations;
using Workstream.Data.Repositories;
using Workstream.GitHub;
using Workstream.Mcp;
using Workstream.Mcp.Notifications;
using Workstream.Slack;
using Workstream.Api.Webhooks;

// Npgsql 6+ defaults timestamptz → DateTime; the domain records use DateTimeOffset.
// Flipping this switch (which must run before any Npgsql type is loaded) restores the
// 5.x mapping: timestamptz ↔ DateTimeOffset, timestamp ↔ DateTime. We always write UTC,
// so the strictness this switch loses doesn't bite us.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// ----- Configuration -----
var dbConn         = BuildDbConnectionString(builder.Configuration);
var adminToken     = ResolveSecret(builder.Configuration, "WORKSTREAM_ADMIN_TOKEN",   "WORKSTREAM_ADMIN_TOKEN_FILE");

builder.Services.Configure<NpgsqlConnectionFactoryOptions>(o => o.ConnectionString = dbConn);
builder.Services.AddSingleton<TokenResolutionMiddleware.IOptions>(
    new TokenResolutionMiddleware.Options { AdminToken = adminToken });

// ----- Data layer -----
builder.Services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IProjectRepository, ProjectRepository>();
builder.Services.AddSingleton<IPlanRepository, PlanRepository>();
builder.Services.AddSingleton<IPlanTypeRepository, PlanTypeRepository>();
builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
builder.Services.AddSingleton<IFindingRepository, FindingRepository>();
builder.Services.AddSingleton<IAttemptRepository, AttemptRepository>();
builder.Services.AddSingleton<IVerdictRepository, VerdictRepository>();
builder.Services.AddSingleton<IEventRepository, EventRepository>();
builder.Services.AddSingleton<IOutboxRepository, OutboxRepository>();

// ----- Core services -----
builder.Services.AddSingleton<IPlanTypeCache, PostgresPlanTypeCache>();
builder.Services.AddSingleton<StateMachineService>();

// ----- Outbox enqueue adapters -----
builder.Services.AddSingleton<IBoardSyncEnqueue, OutboxBoardSyncEnqueue>();
builder.Services.AddSingleton<ISlackNotifyEnqueue, OutboxSlackNotifyEnqueue>();

// ----- GitHub Projects V2 -----
builder.Services.Configure<GitHubAppOptions>(o =>
{
    if (int.TryParse(builder.Configuration["WORKSTREAM_GH_APP_ID"], out var appId)) o.AppId = appId;
    o.PrivateKeyPath = builder.Configuration["WORKSTREAM_GH_APP_KEY_PATH"] ?? "";
    if (long.TryParse(builder.Configuration["WORKSTREAM_GH_INSTALLATION_ID"], out var inst)) o.InstallationId = inst;
    o.WebhookSecret = ResolveSecret(builder.Configuration, "WORKSTREAM_GH_WEBHOOK_SECRET", "WORKSTREAM_GH_WEBHOOK_SECRET_FILE");
});
builder.Services.AddSingleton<GitHubAppAuthService>();
builder.Services.AddHttpClient<ProjectsV2Client>();
builder.Services.AddHostedService<BoardSyncWorker>();

// ----- Slack -----
builder.Services.AddSingleton<ISlackBotTokenResolver, FileSlackBotTokenResolver>();
builder.Services.AddHttpClient<SlackClient>();
builder.Services.AddHostedService<SlackNotifyWorker>();
builder.Services.Configure<SlackAppOptions>(o =>
{
    o.SigningSecret = ResolveSecret(builder.Configuration,
        "WORKSTREAM_SLACK_SIGNING_SECRET", "WORKSTREAM_SLACK_SIGNING_SECRET_FILE");
});

// ----- MCP tools -----
builder.Services.AddWorkstreamMcpTools();

// ----- Logging -----
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var app = builder.Build();

// ----- Migrations on startup -----
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    var migrationsDir = ResolveMigrationsDir();
    var runner = new SqlMigrationRunner(factory, migrationsDir);
    await runner.ApplyAsync().ConfigureAwait(false);
}

// ----- Middleware pipeline -----
// TokenResolutionMiddleware rewrites /{token}/foo → /foo, so it must run BEFORE routing
// decides which endpoint matches. Explicit UseRouting() suppresses the implicit one that
// minimal-API would otherwise insert at the head of the pipeline.
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<TokenResolutionMiddleware>();
app.UseRouting();
app.UseMiddleware<RateLimitMiddleware>();

app.MapStatus();
app.MapMcp();
app.MapProjectsWebhook();
app.MapSlackInteractivity();
app.MapAdmin();

await app.RunAsync().ConfigureAwait(false);


// -------- helpers --------
static string ResolveSecret(IConfiguration cfg, string envKey, string? fileEnvKey)
{
    var value = cfg[envKey] ?? Environment.GetEnvironmentVariable(envKey) ?? "";
    if (string.IsNullOrEmpty(value) && fileEnvKey is not null)
    {
        var file = cfg[fileEnvKey] ?? Environment.GetEnvironmentVariable(fileEnvKey);
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
            value = File.ReadAllText(file).Trim();
    }
    return value;
}

// Resolves the Postgres connection string. If WORKSTREAM_DB_CONNECTION contains the
// __from_secret__ placeholder (the pattern from §12.1), substitute it with the contents
// of WORKSTREAM_DB_PASSWORD_FILE.
static string BuildDbConnectionString(IConfiguration cfg)
{
    var conn = cfg["WORKSTREAM_DB_CONNECTION"] ?? Environment.GetEnvironmentVariable("WORKSTREAM_DB_CONNECTION") ?? "";
    if (string.IsNullOrEmpty(conn))
        throw new InvalidOperationException("WORKSTREAM_DB_CONNECTION must be set");
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

static string ResolveMigrationsDir()
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
