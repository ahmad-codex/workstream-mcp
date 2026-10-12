using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workstream.Api.Demo;
using Workstream.Api.Middleware;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;
using Workstream.Mcp;

namespace Workstream.Api.Mcp;

/// <summary>
/// MCP over stdio: newline-delimited JSON-RPC on stdin/stdout, logs on stderr. Started with
/// <c>--stdio</c> or <c>WORKSTREAM_TRANSPORT=stdio</c>.
///
/// With <c>WORKSTREAM_DB_CONNECTION</c> and <c>WORKSTREAM_USER_TOKEN</c> set it acts as that
/// user against an existing database, like the HTTP endpoint (the HTTP server's workers still
/// drain the outboxes). With no database it runs in discovery mode: <c>initialize</c> and
/// <c>tools/list</c> work, and every <c>tools/call</c> returns a <c>not_configured</c> error.
/// It never runs migrations or background workers.
/// </summary>
public static class StdioServer
{
    public const string TokenEnvVar = "WORKSTREAM_USER_TOKEN";

    public static bool IsRequested(string[] args, IConfiguration cfg) =>
        args.Contains("--stdio", StringComparer.Ordinal)
        || string.Equals(cfg["WORKSTREAM_TRANSPORT"], "stdio", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
            o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        var conn = HostSetup.BuildDbConnectionString(builder.Configuration, required: false);
        var discovery = conn is null;
        // Discovery mode still constructs the tools (for names and schemas); the placeholder
        // connection string is never opened because tools/call is refused before any tool runs.
        builder.Services.AddWorkstreamData(conn ?? "Host=unconfigured");
        builder.Services.AddWorkstreamMcpTools();

        using var host = builder.Build();
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("workstream.stdio");

        RequestContext? ctx = null;
        if (discovery)
        {
            log.LogWarning("no WORKSTREAM_DB_CONNECTION: discovery mode, tools can be listed but not called");
        }
        else
        {
            var token = builder.Configuration[TokenEnvVar] ?? "";
            ctx = await ResolveUserAsync(host.Services, token, DemoMode.IsEnabled(builder.Configuration)).ConfigureAwait(false);
            if (ctx is null)
            {
                log.LogError("{Var} is missing, unknown, revoked, or the demo token outside demo mode", TokenEnvVar);
                return 2;
            }
            log.LogInformation("stdio MCP server ready as {User}", ctx.GithubUsername);
        }

        var tools = host.Services.GetServices<IMcpTool>().ToDictionary(t => t.Name);
        using var stdin = new StreamReader(Console.OpenStandardInput());
        await using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        await ServeAsync(stdin, stdout, ctx, tools, CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Reads requests until end of input. Notifications (no id) get no response.</summary>
    public static async Task ServeAsync(
        TextReader input, TextWriter output, RequestContext? ctx,
        IReadOnlyDictionary<string, IMcpTool> tools, CancellationToken ct)
    {
        string? line;
        while ((line = await input.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            object response;
            try
            {
                var req = JsonSerializer.Deserialize<McpProtocol.JsonRpcRequest>(line, McpProtocol.JsonOpts);
                if (req is null)
                {
                    response = McpProtocol.Error(null, -32600, "invalid request");
                }
                else if (req.Id is null)
                {
                    continue;
                }
                else
                {
                    // Fresh clock and trace id per request, as the HTTP middleware does.
                    var callCtx = ctx is null ? null
                        : ctx with { Now = DateTimeOffset.UtcNow, TraceId = Guid.NewGuid().ToString("N") };
                    response = await McpProtocol.HandleAsync(req, callCtx, tools, ct).ConfigureAwait(false);
                }
            }
            catch (JsonException ex)
            {
                response = McpProtocol.Error(null, -32700, $"parse error: {ex.Message}");
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(response, McpProtocol.JsonOpts)).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<RequestContext?> ResolveUserAsync(IServiceProvider services, string token, bool demoEnabled)
    {
        if (string.IsNullOrEmpty(token) || !DemoMode.IsTokenAllowed(token, demoEnabled)) return null;
        var users = services.GetRequiredService<IUserRepository>();
        var user = await users.ResolveByTokenAsync(token).ConfigureAwait(false);
        if (user is null) return null;
        if (await users.IsTokenRevokedAsync(TokenResolutionMiddleware.HashToken(token)).ConfigureAwait(false)) return null;
        return new RequestContext(
            ActorId: user.Id,
            ActorType: user.ActorType,
            GithubUsername: user.GithubUsername,
            IsAdmin: user.IsAdmin,
            CanOverrideVerdict: user.CanOverrideVerdict,
            CanArchivePlan: user.CanArchivePlan,
            CanMarkNeedsHumanReview: user.CanMarkNeedsHumanReview,
            TraceId: Guid.NewGuid().ToString("N"),
            Now: DateTimeOffset.UtcNow,
            DisplayName: user.DisplayName);
    }
}
