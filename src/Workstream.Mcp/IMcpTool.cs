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
    string Name        { get; }
    string Description { get; }
    Type   InputType   { get; }

    Task<ToolResult> ExecuteAsync(object input, RequestContext ctx, CancellationToken ct);
}

public abstract class McpTool<TInput, TOutput> : IMcpTool
    where TInput  : notnull
    where TOutput : notnull
{
    public abstract string Name        { get; }
    public abstract string Description { get; }
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
