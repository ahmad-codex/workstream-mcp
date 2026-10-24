using System;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;

namespace Workstream.Mcp;

/// <summary>
/// Pure tool contract. Each tool is a class with a typed input (deserialized from the JSON
/// arguments by the MCP transport) and a typed output. The transport layer in
/// <c>Workstream.Api</c> registers these with the MCP SDK; tools themselves never touch
/// HTTP, JSON-RPC, or any other transport concern. This keeps them trivially unit-testable.
/// </summary>
public interface IMcpTool
{
    string    Name        { get; }
    string    Description { get; }
    Type      InputType   { get; }
    ToolHints Hints       { get; }

    Task<ToolResult> ExecuteAsync(object input, RequestContext ctx, CancellationToken ct);
}

/// <summary>
/// MCP tool annotations, so clients can tell a read from a change and confirm the risky ones.
/// Every tool declares one; there is no default, so a new tool can't ship unlabelled.
/// </summary>
public sealed record ToolHints(bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld = false)
{
    /// <summary>Reads state; safe to repeat.</summary>
    public static readonly ToolHints Read = new(ReadOnly: true, Destructive: false, Idempotent: true);

    /// <summary>Adds or advances work (a claim, a submission, a new row); repeating does it again.</summary>
    public static readonly ToolHints Additive = new(ReadOnly: false, Destructive: false, Idempotent: false);

    /// <summary>Moves an entity to a state; repeating with the same arguments changes nothing more.</summary>
    public static readonly ToolHints Transition = new(ReadOnly: false, Destructive: false, Idempotent: true);

    /// <summary>Replaces or discards state, or bypasses the normal flow.</summary>
    public static readonly ToolHints Overwrite = new(ReadOnly: false, Destructive: true, Idempotent: true);
}

public abstract class McpTool<TInput, TOutput> : IMcpTool
    where TInput  : notnull
    where TOutput : notnull
{
    public abstract string    Name        { get; }
    public abstract string    Description { get; }
    public abstract ToolHints Hints       { get; }
    public Type InputType => typeof(TInput);

    async Task<ToolResult> IMcpTool.ExecuteAsync(object input, RequestContext ctx, CancellationToken ct)
    {
        try
        {
            var typed = (TInput)input;
            var output = await RunAsync(typed, ctx, ct).ConfigureAwait(false);
            return ToolResult.Success(output);
        }
        catch (WorkstreamException wx)
        {
            return ToolResult.Failure(wx.Error);
        }
    }

    protected abstract Task<TOutput> RunAsync(TInput input, RequestContext ctx, CancellationToken ct);
}
