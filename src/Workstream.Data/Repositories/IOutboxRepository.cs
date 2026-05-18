using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Workstream.Data.Repositories;

public interface IOutboxRepository
{
    Task<long> EnqueueBoardSyncAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus, CancellationToken ct = default);
    Task<IReadOnlyList<BoardSyncRow>> ClaimBoardSyncBatchAsync(int batchSize, CancellationToken ct = default);
    Task MarkBoardSyncResultAsync(long id, string result, string? error, string? githubResponseId, TimeSpan? retryDelay, CancellationToken ct = default);
    Task<bool> RecentSyncMarkerExistsAsync(Guid boardItemTaskId, TimeSpan within, CancellationToken ct = default);

    Task<long> EnqueueSlackAsync(SlackNotifyRow row, CancellationToken ct = default);
    Task<IReadOnlyList<SlackNotifyRow>> ClaimSlackBatchAsync(int batchSize, CancellationToken ct = default);
    Task MarkSlackResultAsync(long id, string result, string? error, string? slackTs, TimeSpan? retryDelay, CancellationToken ct = default);
    Task<string?> GetParentSlackTsAsync(string entityType, Guid entityId, CancellationToken ct = default);
}

// Property-style records (not positional) so Dapper uses the parameterless constructor +
// init-only setters with lenient per-column type conversion (Guid? ↔ Guid, DateTimeOffset ↔
// DateTime). Positional records force Dapper into strict constructor-signature matching
// which fails on those nullable / timestamp pairings.
public sealed record BoardSyncRow
{
    public long   Id            { get; init; }
    public Guid?  TaskId        { get; init; }
    public Guid?  BoardId       { get; init; }
    public string TargetColumn  { get; init; } = "";
    public string TargetStatus  { get; init; } = "";
    public Guid   SyncMarker    { get; init; }
    public int    Attempts      { get; init; }
    public DateTime NextAttemptAt { get; init; }
    public string Result        { get; init; } = "pending";
}

public sealed record SlackNotifyRow
{
    public long    Id               { get; init; }
    public long?   EventId          { get; init; }
    public Guid?   ProjectId        { get; init; }
    public Guid?   PlanId           { get; init; }
    public string  EntityType       { get; init; } = "";
    public Guid    EntityId         { get; init; }
    public string  ChannelId        { get; init; } = "";
    public string  NotificationType { get; init; } = "";
    public string  Body             { get; init; } = "";
    public string? ThreadTs         { get; init; }
    public string? SlackTs          { get; init; }
    public int     Attempts         { get; init; }
    public DateTime NextAttemptAt   { get; init; }
    public string  Result           { get; init; } = "pending";
}
