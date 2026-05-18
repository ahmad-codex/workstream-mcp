using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Mcp;
using Workstream.Api.Middleware;

namespace Workstream.Api.Mcp;

/// <summary>
/// Minimal MCP-over-HTTP endpoint. Implements the three methods we care about:
/// <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>. Streamable HTTP is just plain
/// POST request → JSON response for our request/response tools (no streaming today).
/// Sticks to the MCP wire shape so a future swap to the official SDK is mechanical.
/// </summary>
public static class JsonRpcEndpoint
{
    // MCP wire format is camelCase: protocolVersion, serverInfo, listChanged, inputSchema, etc.
    // PropertyNameCaseInsensitive lets us accept tool arguments in either camelCase or
    // snake_case — orchestrator LLMs are sloppy about casing, so being lenient on input
    // costs nothing.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

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

        JsonRpcRequest? req;
        try
        {
            req = await JsonSerializer.DeserializeAsync<JsonRpcRequest>(http.Request.Body, JsonOpts).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(http, null, -32700, $"parse error: {ex.Message}").ConfigureAwait(false);
            return;
        }
        if (req is null)
        {
            await WriteErrorAsync(http, null, -32600, "invalid request").ConfigureAwait(false);
            return;
        }

        switch (req.Method)
        {
            case "initialize":
                await WriteResultAsync(http, req.Id, new InitializeResult(
                    ProtocolVersion: "2025-06-18",
                    ServerInfo: new ServerInfo("workstream-mcp", "0.1.0"),
                    Capabilities: new { tools = new { listChanged = false } })).ConfigureAwait(false);
                break;

            case "tools/list":
                await WriteResultAsync(http, req.Id, new
                {
                    tools = tools.Values.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = BuildInputSchema(t.InputType),
                    }).ToArray(),
                }).ConfigureAwait(false);
                break;

            case "tools/call":
                await CallToolAsync(http, req, ctx, tools).ConfigureAwait(false);
                break;

            default:
                await WriteErrorAsync(http, req.Id, -32601, $"unknown method '{req.Method}'").ConfigureAwait(false);
                break;
        }
    }

    private static async Task CallToolAsync(HttpContext http, JsonRpcRequest req, RequestContext ctx, Dictionary<string, IMcpTool> tools)
    {
        if (req.Params is not JsonElement p)
        {
            await WriteErrorAsync(http, req.Id, -32602, "tools/call requires params").ConfigureAwait(false);
            return;
        }
        var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (name is null || !tools.TryGetValue(name, out var tool))
        {
            await WriteErrorAsync(http, req.Id, -32602, $"unknown tool '{name}'").ConfigureAwait(false);
            return;
        }

        var args = p.TryGetProperty("arguments", out var a) ? a : default;
        object input;
        try
        {
            input = args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null
                ? Activator.CreateInstance(tool.InputType)!
                : JsonSerializer.Deserialize(args.GetRawText(), tool.InputType, JsonOpts)!;
        }
        catch (Exception ex)
        {
            await WriteResultAsync(http, req.Id, new
            {
                ok = false,
                error = new { code = ErrorCodes.ValidationError, message = $"invalid arguments: {ex.Message}" },
            }).ConfigureAwait(false);
            return;
        }

        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(input, ctx, http.RequestAborted).ConfigureAwait(false);
        }
        catch (WorkstreamException wx)
        {
            result = ToolResult.Failure(wx.Error);
        }
        catch (Exception ex)
        {
            result = ToolResult.Failure(new WorkstreamError(ErrorCodes.ServiceUnavailable, ex.Message));
        }

        var payload = result.Ok
            ? new { ok = true,  data = result.Data, error = (object?)null }
            : new { ok = false, data = (object?)null, error = (object?)result.Error };
        await WriteResultAsync(http, req.Id, new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload, JsonOpts) } },
            isError = !result.Ok,
        }).ConfigureAwait(false);
    }

    private static object BuildInputSchema(Type t)
    {
        // Minimal JSON Schema (object with properties derived from the record's primary
        // constructor parameters). Good enough for the LLM to grok parameter names + types.
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (ctor is not null)
        {
            foreach (var p in ctor.GetParameters())
            {
                props[p.Name!] = new { type = MapJsonType(p.ParameterType) };
                if (!p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) is null
                    && !(p.ParameterType.IsClass && Nullable.GetUnderlyingType(p.ParameterType) is null))
                {
                    required.Add(p.Name!);
                }
            }
        }
        return new { type = "object", properties = props, required = required.ToArray() };
    }

    private static string MapJsonType(Type t)
    {
        var nt = Nullable.GetUnderlyingType(t) ?? t;
        if (nt == typeof(string)) return "string";
        if (nt == typeof(Guid)) return "string";
        if (nt == typeof(int) || nt == typeof(long)) return "integer";
        if (nt == typeof(double) || nt == typeof(decimal)) return "number";
        if (nt == typeof(bool)) return "boolean";
        if (nt.IsArray || (nt.IsGenericType && nt.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))) return "array";
        return "object";
    }

    private static Task WriteResultAsync(HttpContext http, object? id, object result)
    {
        http.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(http.Response.Body, new { jsonrpc = "2.0", id, result }, JsonOpts);
    }

    private static Task WriteErrorAsync(HttpContext http, object? id, int code, string message)
    {
        http.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(http.Response.Body, new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOpts);
    }

    public sealed record JsonRpcRequest(string Jsonrpc, object? Id, string Method, JsonElement? Params);
    public sealed record InitializeResult(string ProtocolVersion, ServerInfo ServerInfo, object Capabilities);
    public sealed record ServerInfo(string Name, string Version);
}
