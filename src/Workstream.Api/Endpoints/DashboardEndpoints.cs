using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workstream.Api.Middleware;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Workstream.Mcp.Tools.Discovery;
using Workstream.Mcp.Tools.Forensic;

namespace Workstream.Api.Endpoints;

/// <summary>
/// Read-only JSON for the web dashboard, served under the caller's token like <c>/mcp</c>
/// (<c>/{token}/api/...</c>). Plan summaries come from the same MCP tools agents call, so the
/// dashboard and the agents see one view of the state. Nothing here writes.
/// </summary>
public static class DashboardEndpoints
{
    public const string UiPath = "/ui";

    public static void MapDashboard(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/projects", async (HttpContext http, IEnumerable<IMcpTool> tools) =>
            await RunToolAsync(http, tools, "list_projects", new ListProjectsInput()).ConfigureAwait(false));

        api.MapGet("/projects/{projectId:guid}/plans", async (Guid projectId, HttpContext http, IEnumerable<IMcpTool> tools) =>
            await RunToolAsync(http, tools, "list_plans", new ListPlansInput(projectId)).ConfigureAwait(false));

        api.MapGet("/plans/{planId:guid}/board", async (Guid planId, HttpContext http, IEnumerable<IMcpTool> tools, IBoardQueries board) =>
        {
            if (!TryGetContext(http, out var ctx)) return Results.NotFound();
            var summary = await Tool(tools, "get_plan_dashboard").ExecuteAsync(new GetPlanDashboardInput(planId), ctx, http.RequestAborted).ConfigureAwait(false);
            if (!summary.Ok) return Envelope(summary);

            var tasks = await board.ListTasksAsync(planId, http.RequestAborted).ConfigureAwait(false);
            var findings = await board.ListFindingsAsync(planId, http.RequestAborted).ConfigureAwait(false);
            var dashboard = (GetPlanDashboardOutput)summary.Data!;

            return Results.Json(new
            {
                ok = true,
                data = new
                {
                    serverTime = DateTimeOffset.UtcNow,
                    plan = dashboard.Plan,
                    byStatus = dashboard.ByStatus,
                    stuckWork = dashboard.StuckWork,
                    recentEvents = dashboard.RecentEvents,
                    tasks,
                    findings,
                },
            });
        });

        api.MapGet("/events/{entityType}/{entityId:guid}", async (string entityType, Guid entityId, HttpContext http, IEnumerable<IMcpTool> tools, IBoardQueries board) =>
        {
            if (!TryGetContext(http, out var ctx)) return Results.NotFound();
            var result = await Tool(tools, "get_event_log").ExecuteAsync(new GetEventLogInput(entityType, entityId, Limit: 200), ctx, http.RequestAborted).ConfigureAwait(false);
            if (!result.Ok) return Envelope(result);

            var events = ((GetEventLogOutput)result.Data!).Events;
            var names = await board.GetActorNamesAsync(events.Where(e => e.ActorId.HasValue).Select(e => e.ActorId!.Value), http.RequestAborted).ConfigureAwait(false);
            return Results.Json(new
            {
                ok = true,
                data = events.Select(e => new
                {
                    e.Id,
                    e.At,
                    actor = e.ActorId is { } a && names.TryGetValue(a, out var n) ? n : null,
                    e.EventType,
                    e.FromState,
                    e.ToState,
                    e.Payload,
                }),
            });
        });

        // The page itself. "/{token}/ui" redirects to "/{token}/ui/" so the relative asset and
        // API paths in index.html resolve under the token.
        // Routing matches "/ui" and "/ui/" alike, so the redirect checks the raw path.
        app.MapGet(UiPath, (HttpContext http) =>
            http.Request.Path.Value!.EndsWith('/') ? Results.File("ui/index.html", "text/html") : Results.Redirect("ui/"));
        app.MapFallbackToFile(UiPath + "/{*path}", "ui/index.html");
    }

    private static async Task<IResult> RunToolAsync(HttpContext http, IEnumerable<IMcpTool> tools, string name, object input)
    {
        if (!TryGetContext(http, out var ctx)) return Results.NotFound();
        var result = await Tool(tools, name).ExecuteAsync(input, ctx, http.RequestAborted).ConfigureAwait(false);
        return Envelope(result);
    }

    private static IMcpTool Tool(IEnumerable<IMcpTool> tools, string name) => tools.First(t => t.Name == name);

    private static IResult Envelope(ToolResult result) =>
        result.Ok
            ? Results.Json(new { ok = true, data = result.Data })
            : Results.Json(new { ok = false, error = result.Error }, statusCode: StatusCodes.Status400BadRequest);

    private static bool TryGetContext(HttpContext http, out RequestContext ctx)
    {
        ctx = null!;
        if (http.Items[TokenResolutionMiddleware.ContextKey] is not RequestContext rc || rc.ActorId == Guid.Empty)
            return false;
        ctx = rc;
        return true;
    }
}
