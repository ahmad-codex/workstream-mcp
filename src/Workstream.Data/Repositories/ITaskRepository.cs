using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

/// <summary>
/// Task persistence. Methods that mutate claim or status state are atomic — they perform
/// the row lock, the claim check, the state mutation, and the event emission inside a
/// single transaction (§6.3).
/// </summary>
public interface ITaskRepository
{
    Task<WorkTask?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<WorkTask>> ListByPlanAsync(Guid planId, CancellationToken ct = default);

    Task<WorkTask> InsertAsync(WorkTaskInsert input, CancellationToken ct = default);

    /// <summary>
    /// Atomically claim the next available task on the plan for the given role.
    /// Returns null if no claimable work exists. Implements the FOR UPDATE SKIP LOCKED
    /// query in §6.1.
    /// </summary>
    Task<ClaimedTask?> ClaimNextAsync(
        Guid planId,
        Guid actorId,
        string role,
        TimeSpan ttl,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically claim a specific task by id. Fails (returns null) if the task is held by
    /// another actor with an unexpired claim.
    /// </summary>
    Task<ClaimedTask?> ClaimSpecificAsync(
        Guid taskId,
        Guid actorId,
        string role,
        TimeSpan ttl,
        CancellationToken ct = default);

    /// <summary>
    /// Release a claim. Returns the updated row (claim columns cleared, status possibly reset).
    /// </summary>
    Task<WorkTask?> ReleaseClaimAsync(
        Guid claimToken,
        Guid actorId,
        string? reason,
        bool resetStatusToPending,
        CancellationToken ct = default);

    /// <summary>
    /// Update the task status atomically gated on the claim token. Returns the updated row,
    /// or null if the claim token does not match (caller surfaces <c>stale_claim</c>).
    /// </summary>
    Task<WorkTask?> UpdateStatusWithClaimAsync(
        Guid claimToken,
        Guid actorId,
        string newStatus,
        bool clearClaim,
        string eventType,
        string? eventPayloadJson,
        CancellationToken ct = default);

    /// <summary>
    /// Override the status without holding a claim. Requires the caller to have already
    /// verified <c>can_override_verdict</c>. Writes an <c>override</c> event.
    /// </summary>
    Task<WorkTask?> OverrideStatusAsync(
        Guid taskId,
        Guid actorId,
        string newStatus,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Hourly stuck-work sweeper: resets <c>claimed</c> tasks whose claim TTL has elapsed
    /// back to <c>pending</c> so <see cref="ClaimNextAsync"/> can pick them up.
    /// Returns the number of rows swept.
    /// </summary>
    Task<int> SweepExpiredClaimsAsync(CancellationToken ct = default);

    /// <summary>
    /// Persist the GitHub Projects V2 item node id (<c>PVTI_…</c>) and its numeric
    /// databaseId for a task. Called by the board-sync worker the first time it
    /// processes a task that doesn't yet have them (lazy create on first transition).
    /// The number is optional because some legacy rows may only have the node id;
    /// the worker backfills it on the next sync.
    /// </summary>
    Task SetGithubBoardItemIdAsync(Guid taskId, string boardItemId, long? boardItemNumber, CancellationToken ct = default);

    /// <summary>Return the numeric databaseId previously stored for this task, or null.</summary>
    Task<long?> GetGithubBoardItemNumberAsync(Guid taskId, CancellationToken ct = default);
}

public sealed record WorkTaskInsert(
    Guid     PlanId,
    Guid?    PhaseId,
    string   ExternalKey,
    string   Title,
    string?  Description,
    string[]? Paths,
    string?  ReferencePointer,
    int      Priority);

public sealed record ClaimedTask(WorkTask Task, Guid ClaimToken);
