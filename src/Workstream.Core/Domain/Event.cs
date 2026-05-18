using System;

namespace Workstream.Core.Domain;

public sealed record Event(
    long    Id,
    DateTimeOffset At,
    Guid?   ActorId,
    string  EntityType,
    Guid    EntityId,
    string  EventType,
    string? FromState,
    string? ToState,
    string  Payload);   // JSONB string

/// <summary>
/// Standard event types written to the <c>events</c> table.
/// Application code emits these alongside the trigger-emitted <c>created</c> and
/// <c>status_changed</c> backstops.
/// </summary>
public static class EventType
{
    public const string Created            = "created";
    public const string StatusChanged      = "status_changed";
    public const string Claimed            = "claimed";
    public const string Released           = "released";
    public const string ClaimExpired       = "claim_expired";
    public const string AttemptSubmitted   = "attempt_submitted";
    public const string VerdictSubmitted   = "verdict_submitted";
    public const string Override           = "override";
    public const string BoardSynced        = "board_synced";
    public const string BoardSyncFailed    = "board_sync_failed";
    public const string SlackNotified      = "slack_notified";
    public const string CommitRecorded     = "commit_recorded";
    public const string PlanActivated      = "plan_activated";
    public const string PlanCompleted      = "plan_completed";
    public const string TaskCreatedFromBoard = "task_created_from_board";
    public const string Escalated          = "escalated";
}
