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

public sealed record BoardSyncRow(
    long  Id,
    Guid? TaskId,
    Guid? BoardId,
    string TargetColumn,
    string TargetStatus,
    Guid  SyncMarker,
    int   Attempts,
    DateTimeOffset NextAttemptAt,
    string Result);

public sealed record SlackNotifyRow(
    long Id,
    long? EventId,
    Guid? ProjectId,
    Guid? PlanId,
    string EntityType,
    Guid   EntityId,
    string ChannelId,
    string NotificationType,
    string Body,
    string? ThreadTs,
    string? SlackTs,
    int   Attempts,
    DateTimeOffset NextAttemptAt,
    string Result);
