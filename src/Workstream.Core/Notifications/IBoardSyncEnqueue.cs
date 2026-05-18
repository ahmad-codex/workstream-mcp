using System;
using System.Threading;
using System.Threading.Tasks;

namespace Workstream.Mcp.Notifications;

/// <summary>
/// Enqueues a board update for the outbox worker (§7.5). Always succeeds (writes one row).
/// The MCP tools call this inside the same transaction as the business mutation; the
/// outbox worker drains the queue asynchronously and tolerates GitHub API outages.
/// </summary>
public interface IBoardSyncEnqueue
{
    Task EnqueueAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus, CancellationToken ct = default);
}
