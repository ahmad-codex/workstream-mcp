using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Workstream.Data.Repositories;

public interface IOutboxRepository
{
    Task<long> EnqueueBoardSyncAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus, string? assigneeGithubUsername = null, CancellationToken ct = default);
    Task<IReadOnlyList<BoardSyncRow>> ClaimBoardSyncBatchAsync(int batchSize, CancellationToken ct = default);
    Task MarkBoardSyncResultAsync(long id, string result, string? error, string? githubResponseId, TimeSpan? retryDelay, CancellationToken ct = default);
    Task<bool> RecentSyncMarkerExistsAsync(Guid boardItemTaskId, TimeSpan within, CancellationToken ct = default);

    Task<long> EnqueueSlackAsync(SlackNotifyRow row, CancellationToken ct = default);
    Task<IReadOnlyList<SlackNotifyRow>> ClaimSlackBatchAsync(int batchSize, CancellationToken ct = default);
    Task MarkSlackResultAsync(long id, string result, string? error, string? slackTs, TimeSpan? retryDelay, CancellationToken ct = default);
    Task<string?> GetParentSlackTsAsync(string entityType, Guid entityId, CancellationToken ct = default);

    Task<long> EnqueueAuditDispatchAsync(Guid projectId, Guid? requestedBy, string action = "run", CancellationToken ct = default);
    Task<IReadOnlyList<AuditDispatchRow>> ClaimAuditDispatchBatchAsync(int batchSize, CancellationToken ct = default);
    Task MarkAuditDispatchResultAsync(long id, string status, string? result, string? error, TimeSpan? retryDelay, CancellationToken ct = default);

    Task<long> EnqueueMilestoneSyncAsync(Guid planId, string action, string? outcome, string? reason, CancellationToken ct = default);
    Task<IReadOnlyList<MilestoneSyncRow>> ClaimMilestoneSyncBatchAsync(int batchSize, CancellationToken ct = default);
    Task MarkMilestoneSyncResultAsync(long id, string result, string? error, TimeSpan? retryDelay, CancellationToken ct = default);
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
    public string? AssigneeGithubUsername { get; init; }
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
    // Rendered Block Kit `blocks` array (JSON). When present the worker posts it inside
    // the coloured attachment; Body becomes the attachment `fallback`. NULL => post the
    // plain Body. Resolved at enqueue time from the same tokens as Body.
    public string? BlocksJson       { get; init; }
    // Attachment colour bar (hex, e.g. "#7C3AED"). NULL => post as a plain message.
    // Resolved at enqueue time from the plan-type's role_personas.
    public string? Color            { get; init; }
    // Attachment author row. For a human actor AuthorIcon is the public GitHub avatar
    // and AuthorLink the profile, so the message shows the person's photo; all NULL for
    // AI actors (their role persona shows in the message body instead).
    public string? AuthorName       { get; init; }
    public string? AuthorIcon       { get; init; }
    public string? AuthorLink       { get; init; }
    public string? ThreadTs         { get; init; }
    public string? SlackTs          { get; init; }
    public int     Attempts         { get; init; }
    public DateTime NextAttemptAt   { get; init; }
    public string  Result           { get; init; } = "pending";
}

/// <summary>One queued GitHub-milestone operation for a plan's audit run.</summary>
public sealed record MilestoneSyncRow
{
    public long     Id            { get; init; }
    public Guid     PlanId        { get; init; }
    public string   Action        { get; init; } = "";   // 'create' | 'close'
    public string?  Outcome       { get; init; }         // 'Completed' | 'Canceled' (close)
    public string?  Reason        { get; init; }
    public int      Attempts      { get; init; }
    public DateTime NextAttemptAt { get; init; }
    public string   Result        { get; init; } = "pending";
}

/// <summary>One queued audit-dispatch request: run or cancel a project's audit.</summary>
public sealed record AuditDispatchRow
{
    public long     Id            { get; init; }
    public Guid     ProjectId     { get; init; }
    public Guid?    RequestedBy   { get; init; }
    public string   Action        { get; init; } = "run";   // 'run' | 'cancel'
    public int      Attempts      { get; init; }
    public DateTime NextAttemptAt { get; init; }
    public string   Status        { get; init; } = "pending";
}
