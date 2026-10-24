using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Mcp;

namespace Workstream.Api.Mcp;

/// <summary>
/// Transport-independent MCP JSON-RPC handling: <c>initialize</c>, <c>tools/list</c> and
/// <c>tools/call</c>. The HTTP endpoint (<see cref="JsonRpcEndpoint"/>) and the stdio server
/// (<see cref="StdioServer"/>) both call <see cref="HandleAsync"/> and only differ in how
/// they read requests and write the returned envelope.
/// </summary>
public static class McpProtocol
{
    public const string ProtocolVersion = "2025-06-18";

    /// <summary>Error code returned by <c>tools/call</c> when the server has no database.</summary>
    public const string NotConfiguredCode = "not_configured";

    // MCP wire format is camelCase: protocolVersion, serverInfo, listChanged, inputSchema, etc.
    // PropertyNameCaseInsensitive lets us accept tool arguments in either camelCase or
    // snake_case — orchestrator LLMs are sloppy about casing, so being lenient on input
    // costs nothing.
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Handles one request and returns the full JSON-RPC response envelope.
    /// <paramref name="ctx"/> is null in discovery mode (no database configured): introspection
    /// works and every <c>tools/call</c> returns a <c>not_configured</c> tool error.
    /// </summary>
    public static async Task<object> HandleAsync(
        JsonRpcRequest req, RequestContext? ctx, IReadOnlyDictionary<string, IMcpTool> tools, CancellationToken ct)
    {
        switch (req.Method)
        {
            case "initialize":
                return Result(req.Id, new InitializeResult(
                    ProtocolVersion: ProtocolVersion,
                    ServerInfo: new ServerInfo("workstream-mcp", "0.1.0"),
                    Capabilities: new { tools = new { listChanged = false } }));

            case "tools/list":
                return Result(req.Id, new
                {
                    tools = tools.Values.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchema = BuildInputSchema(t.InputType),
                        annotations = new
                        {
                            readOnlyHint    = t.Hints.ReadOnly,
                            destructiveHint = t.Hints.Destructive,
                            idempotentHint  = t.Hints.Idempotent,
                            openWorldHint   = t.Hints.OpenWorld,
                        },
                    }).ToArray(),
                });

            case "tools/call":
                return await CallToolAsync(req, ctx, tools, ct).ConfigureAwait(false);

            case "ping":
                return Result(req.Id, new { });

            default:
                return Error(req.Id, -32601, $"unknown method '{req.Method}'");
        }
    }

    public static object Result(object? id, object result) => new { jsonrpc = "2.0", id, result };

    public static object Error(object? id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };

    private static async Task<object> CallToolAsync(
        JsonRpcRequest req, RequestContext? ctx, IReadOnlyDictionary<string, IMcpTool> tools, CancellationToken ct)
    {
        if (req.Params is not JsonElement p)
            return Error(req.Id, -32602, "tools/call requires params");

        var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (name is null || !tools.TryGetValue(name, out var tool))
            return Error(req.Id, -32602, $"unknown tool '{name}'");

        if (ctx is null)
        {
            return ToolResponse(req.Id, ToolResult.Failure(new WorkstreamError(NotConfiguredCode,
                "This server is running without a database (discovery mode), so tools can be listed but not called. " +
                "Set WORKSTREAM_DB_CONNECTION, or run the demo image: " +
                "docker run --rm -p 127.0.0.1:8080:8080 ghcr.io/ahmad-codex/workstream-mcp:demo")));
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
            return Result(req.Id, new
            {
                ok = false,
                error = new { code = ErrorCodes.ValidationError, message = $"invalid arguments: {ex.Message}" },
            });
        }

        // Optional `as_agent` payload field: lets the caller declare the agent role
        // this call acts as (e.g. "auditor"), so Slack renders the role persona even
        // when the URL token belongs to a human. Display-only — the events table still
        // records the real actor_id. Matched case- and underscore-insensitively.
        var callCtx = ctx;
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
            {
                if (prop.Name.Replace("_", "").Equals("asagent", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String
                    && prop.Value.GetString() is { Length: > 0 } role)
                {
                    callCtx = ctx with { ActingRole = role.Trim() };
                    break;
                }
            }
        }

        ToolResult result;
        try
        {
            result = await tool.ExecuteAsync(input, callCtx, ct).ConfigureAwait(false);
        }
        catch (WorkstreamException wx)
        {
            result = ToolResult.Failure(wx.Error);
        }
        catch (Exception ex)
        {
            result = ToolResult.Failure(new WorkstreamError(ErrorCodes.ServiceUnavailable, ex.Message));
        }

        return ToolResponse(req.Id, result);
    }

    private static object ToolResponse(object? id, ToolResult result)
    {
        var payload = result.Ok
            ? new { ok = true,  data = result.Data, error = (object?)null }
            : new { ok = false, data = (object?)null, error = (object?)result.Error };
        return Result(id, new
        {
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(payload, JsonOpts) } },
            isError = !result.Ok,
        });
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
        // Universal optional field, handled by the endpoint not the tool record:
        // declares the agent role this call acts as so Slack renders that persona.
        props["as_agent"] = new { type = "string" };
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

    public sealed record JsonRpcRequest(string Jsonrpc, object? Id, string Method, JsonElement? Params);
    public sealed record InitializeResult(string ProtocolVersion, ServerInfo ServerInfo, object Capabilities);
    public sealed record ServerInfo(string Name, string Version);
}
