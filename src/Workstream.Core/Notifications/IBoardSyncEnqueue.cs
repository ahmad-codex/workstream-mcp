using System;
using System.Threading;
using System.Threading.Tasks;

namespace Workstream.Mcp.Notifications;

/// <summary>
/// Enqueues a board update for the outbox worker (§7.5). Always succeeds (writes one row).
/// The MCP tools pass the <see cref="IOutboxScope"/> of the business mutation so the row
/// commits with it; the outbox worker drains the queue asynchronously and tolerates GitHub
/// API outages.
/// </summary>
public interface IBoardSyncEnqueue
{
    Task EnqueueAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus, string? assigneeGithubUsername = null, IOutboxScope? scope = null, CancellationToken ct = default);
}
