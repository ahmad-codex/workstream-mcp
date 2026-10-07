using System;
using System.IO;
using Microsoft.Extensions.Options;
using Workstream.Api.Dispatch;
using Workstream.Api.Endpoints;
using Workstream.Api.Mcp;
using Workstream.Api.Middleware;
using Workstream.Core.StateMachine;
using Workstream.Data;
using Workstream.Data.Migrations;
using Workstream.Data.Repositories;
using Workstream.Data.StuckWork;
using Workstream.GitHub;
using Workstream.Mcp;
using Workstream.Mcp.Notifications;
using Workstream.Slack;
using Workstream.Telemetry;
using Workstream.Api.Webhooks;

// Npgsql 6+ defaults timestamptz → DateTime; the domain records use DateTimeOffset.
// Flipping this switch (which must run before any Npgsql type is loaded) restores the
// 5.x mapping: timestamptz ↔ DateTimeOffset, timestamp ↔ DateTime. We always write UTC,
// so the strictness this switch loses doesn't bite us.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// A "dispatcher" replica runs only the audit-dispatch worker — no HTTP surface, no MCP
// tools, no migrations, no Slack/GitHub workers. The default "api" role runs the full
// server. The dispatcher is its own container (Dockerfile.dispatcher) because it needs
// git, tmux and Claude Code, which the lean api image deliberately omits.
var role = builder.Configuration["WORKSTREAM_ROLE"] ?? "api";
var isDispatcher = string.Equals(role, "dispatcher", StringComparison.OrdinalIgnoreCase);

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

// ----- GitHub Projects V2 + Slack (api role only) -----
if (!isDispatcher)
{
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

    builder.Services.AddSingleton<ISlackBotTokenResolver, FileSlackBotTokenResolver>();
    builder.Services.AddHttpClient<SlackClient>();
    builder.Services.AddHostedService<SlackNotifyWorker>();
    builder.Services.Configure<SlackAppOptions>(o =>
    {
        o.SigningSecret = ResolveSecret(builder.Configuration,
            "WORKSTREAM_SLACK_SIGNING_SECRET", "WORKSTREAM_SLACK_SIGNING_SECRET_FILE");
    });
}

// ----- Audit dispatch (both roles register it; only the dispatcher enables it) -----
builder.Services.Configure<AuditDispatchOptions>(o =>
{
    o.Enabled    = string.Equals(builder.Configuration["WORKSTREAM_AUDIT_DISPATCH_ENABLED"],
                                  "true", StringComparison.OrdinalIgnoreCase);
    o.ScriptPath = builder.Configuration["WORKSTREAM_AUDIT_DISPATCH_SCRIPT"] ?? "";
    if (int.TryParse(builder.Configuration["WORKSTREAM_AUDIT_DISPATCH_TIMEOUT"], out var t)) o.TimeoutSeconds = t;
});
builder.Services.AddSingleton<IAuditRunner, ProcessAuditRunner>();
builder.Services.AddHostedService<AuditDispatchWorker>();

// ----- MCP tools and the hourly stuck-work report (api role only) -----
if (!isDispatcher)
{
    builder.Services.AddWorkstreamMcpTools();
    builder.Services.AddHostedService<StuckWorkJob>();
}

// ----- Telemetry: only when an OTLP endpoint is configured -----
var otelEndpoint = builder.Configuration["WORKSTREAM_OTEL_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otelEndpoint))
    builder.Services.AddWorkstreamTelemetry(otelEndpoint);

// ----- Logging -----
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var app = builder.Build();

// ----- Migrations on startup (api role owns the schema) -----
if (!isDispatcher)
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    var migrationsDir = ResolveMigrationsDir();
    var runner = new SqlMigrationRunner(factory, migrationsDir);
    await runner.ApplyAsync().ConfigureAwait(false);
}

if (isDispatcher)
{
    // The dispatcher has no HTTP surface — it just hosts the AuditDispatchWorker.
    // RunAsync keeps the process (and that background service) alive.
    await app.RunAsync().ConfigureAwait(false);
    return;
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
