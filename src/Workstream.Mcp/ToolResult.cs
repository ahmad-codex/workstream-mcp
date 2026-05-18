using Workstream.Core.Errors;

namespace Workstream.Mcp;

/// <summary>
/// Uniform envelope every tool returns. Serializes to the wire as
/// <c>{ "ok": true, "data": {...} }</c> or <c>{ "ok": false, "error": { "code", "message", "details" } }</c>
/// per §9.7.
/// </summary>
public sealed record ToolResult(bool Ok, object? Data, WorkstreamError? Error)
{
    public static ToolResult Success(object data) => new(true, data, null);
    public static ToolResult Failure(WorkstreamError err) => new(false, null, err);
}
