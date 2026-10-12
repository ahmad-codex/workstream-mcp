using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Core.Domain;
using Workstream.Mcp;
using Workstream.Api.Middleware;

namespace Workstream.Api.Mcp;

/// <summary>
/// Minimal MCP-over-HTTP endpoint. Implements the three methods we care about:
/// <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c> (see <see cref="McpProtocol"/>).
/// Streamable HTTP is just plain POST request → JSON response for our request/response tools
/// (no streaming today). Sticks to the MCP wire shape so a future swap to the official SDK is
/// mechanical.
/// </summary>
public static class JsonRpcEndpoint
{
    public static void MapMcp(this IEndpointRouteBuilder app, string pattern = "/mcp")
    {
        app.MapPost(pattern, HandleAsync);
    }

    private static async Task HandleAsync(HttpContext http)
    {
        if (http.Items[TokenResolutionMiddleware.ContextKey] is not RequestContext ctx)
        {
            http.Response.StatusCode = 404;
            return;
        }
        var tools = http.RequestServices.GetServices<IMcpTool>().ToDictionary(t => t.Name);

        McpProtocol.JsonRpcRequest? req;
        try
        {
            req = await JsonSerializer.DeserializeAsync<McpProtocol.JsonRpcRequest>(http.Request.Body, McpProtocol.JsonOpts).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteAsync(http, McpProtocol.Error(null, -32700, $"parse error: {ex.Message}")).ConfigureAwait(false);
            return;
        }
        if (req is null)
        {
            await WriteAsync(http, McpProtocol.Error(null, -32600, "invalid request")).ConfigureAwait(false);
            return;
        }

        var response = await McpProtocol.HandleAsync(req, ctx, tools, http.RequestAborted).ConfigureAwait(false);
        await WriteAsync(http, response).ConfigureAwait(false);
    }

    private static Task WriteAsync(HttpContext http, object envelope)
    {
        http.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(http.Response.Body, envelope, McpProtocol.JsonOpts);
    }
}
