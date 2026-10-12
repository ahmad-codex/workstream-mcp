using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Workstream.Mcp.Tools.Lifecycle;
using Workstream.Mcp.Tools.Setup;

namespace Workstream.Api.Demo;

/// <summary>
/// Demo mode (<c>WORKSTREAM_DEMO=true</c>) for trying the server locally. On startup it seeds a
/// demo admin user with the fixed, public <see cref="Token"/>, plus a project and an active
/// development plan with a few tasks. The token is public knowledge, so it is refused
/// everywhere unless demo mode is on (see <see cref="IsTokenAllowed"/>), even if a demo user
/// row is left behind in a database that later runs without demo mode.
/// </summary>
public static class DemoMode
{
    public const string EnvVar = "WORKSTREAM_DEMO";

    /// <summary>The demo user's URL token. Public and insecure by design.</summary>
    public const string Token = "demo-insecure-token";

    public const string DemoUsername = "demo";
    public const string DemoProjectSlug = "demo-app";

    public static bool IsEnabled(IConfiguration cfg) =>
        string.Equals(cfg[EnvVar], "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsDemoToken(string? token) =>
        string.Equals(token, Token, StringComparison.Ordinal);

    /// <summary>False only for the demo token when demo mode is off.</summary>
    public static bool IsTokenAllowed(string? token, bool demoEnabled) =>
        demoEnabled || !IsDemoToken(token);

    public static void LogWarning(ILogger log, string baseUrl)
    {
        log.LogWarning("============================================================");
        log.LogWarning("DEMO MODE IS ON. NOT FOR PRODUCTION OR SHARED NETWORKS.");
        log.LogWarning("The demo user's token is public: anyone who can reach this port is admin.");
        log.LogWarning("MCP endpoint: {Url}", $"{baseUrl.TrimEnd('/')}/{Token}/mcp");
        log.LogWarning("============================================================");
    }

    /// <summary>
    /// Creates the demo user, project, plan and tasks if they do not exist yet. Project data
    /// is created through the regular MCP tools, so it goes through the same validation,
    /// events and outbox rows as a real client would produce.
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, ILogger log, CancellationToken ct = default)
    {
        var users = services.GetRequiredService<IUserRepository>();
        var user = await users.GetByUsernameAsync(DemoUsername, ct).ConfigureAwait(false)
                   ?? await users.CreateAsync(DemoUsername, "Demo User", ActorType.Human, Token, isAdmin: true, ct).ConfigureAwait(false);
        foreach (var permission in new[] { "can_override_verdict", "can_archive_plan", "can_mark_needs_human_review" })
            await users.SetPermissionAsync(user.Id, permission, true, ct).ConfigureAwait(false);

        var projects = services.GetRequiredService<IProjectRepository>();
        if (await projects.GetBySlugAsync(DemoProjectSlug, ct).ConfigureAwait(false) is not null)
            return;

        var ctx = new RequestContext(
            ActorId: user.Id, ActorType: user.ActorType, GithubUsername: user.GithubUsername,
            IsAdmin: true, CanOverrideVerdict: true, CanArchivePlan: true, CanMarkNeedsHumanReview: true,
            TraceId: Guid.NewGuid().ToString("N"), Now: DateTimeOffset.UtcNow, DisplayName: user.DisplayName);
        var tools = services.GetServices<IMcpTool>().ToDictionary(t => t.Name);

        var project = await RunAsync<CreateProjectOutput>(tools, "create_project",
            new CreateProjectInput(DemoProjectSlug, "Demo App", "Seeded by demo mode"), ctx, ct).ConfigureAwait(false);
        var plan = await RunAsync<CreatePlanOutput>(tools, "create_plan",
            new CreatePlanInput(project.Id, "development", "Demo plan", "Try claiming and submitting a task"), ctx, ct).ConfigureAwait(false);
        await RunAsync<CreateTasksOutput>(tools, "create_tasks", new CreateTasksInput(plan.Id, new List<CreateTasksTaskInput>
        {
            new("DEMO-1", "Add input validation to the signup form", Priority: 3),
            new("DEMO-2", "Write tests for the signup form", Priority: 2, DependsOnExternalKeys: new[] { "DEMO-1" }),
            new("DEMO-3", "Update the README", Priority: 1),
        }), ctx, ct).ConfigureAwait(false);
        await RunAsync<ActivatePlanOutput>(tools, "activate_plan", new ActivatePlanInput(plan.Id), ctx, ct).ConfigureAwait(false);

        log.LogInformation("demo data seeded: project {Slug}, plan {PlanId}", DemoProjectSlug, plan.Id);
    }

    private static async Task<T> RunAsync<T>(
        IReadOnlyDictionary<string, IMcpTool> tools, string name, object input, RequestContext ctx, CancellationToken ct)
    {
        var result = await tools[name].ExecuteAsync(input, ctx, ct).ConfigureAwait(false);
        if (!result.Ok)
            throw new InvalidOperationException($"demo seed: {name} failed: {result.Error?.Code} {result.Error?.Message}");
        return (T)result.Data!;
    }
}
