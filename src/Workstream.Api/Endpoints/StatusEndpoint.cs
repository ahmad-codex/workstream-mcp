using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workstream.Api.Middleware;
using Workstream.Core.Domain;

namespace Workstream.Api.Endpoints;

/// <summary>
/// Bare <c>/{token}</c> endpoint returns a small JSON page so a user can verify the URL works
/// before adding it to Claude (§10.3). Once <see cref="TokenResolutionMiddleware"/> rewrites
/// the path, the request lands here as <c>/</c>.
/// </summary>
public static class StatusEndpoint
{
    public static void MapStatus(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (HttpContext http) =>
        {
            if (http.Items[TokenResolutionMiddleware.ContextKey] is RequestContext rc)
            {
                return Results.Json(new
                {
                    ok = true,
                    actor = new { id = rc.ActorId, github_username = rc.GithubUsername, is_admin = rc.IsAdmin },
                    mcp_endpoint = "POST /mcp (JSON-RPC 2.0)",
                });
            }
            return Results.Json(new { ok = true, service = "workstream-mcp" });
        });

        app.MapGet("/healthz", () => Results.Json(new { ok = true }));
    }
}
