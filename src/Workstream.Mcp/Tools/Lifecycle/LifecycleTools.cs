using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Data.Repositories;
using TaskStatus = Workstream.Core.Domain.TaskStatus;

namespace Workstream.Mcp.Tools.Lifecycle;

// ============================================================================
// mark_task_status
// ============================================================================

public sealed record MarkTaskStatusInput(Guid TaskId, string Status, string Reason);
public sealed record MarkTaskStatusOutput(Guid TaskId, string Status);

public sealed class MarkTaskStatusTool : McpTool<MarkTaskStatusInput, MarkTaskStatusOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly Workstream.Core.StateMachine.IPlanTypeCache _planTypes;
    private readonly Workstream.Mcp.Notifications.IBoardSyncEnqueue _board;
    private readonly Workstream.Mcp.Notifications.ISlackNotifyEnqueue _slack;

    public MarkTaskStatusTool(
        ITaskRepository tasks,
        IPlanRepository plans,
        Workstream.Core.StateMachine.IPlanTypeCache planTypes,
        Workstream.Mcp.Notifications.IBoardSyncEnqueue board,
        Workstream.Mcp.Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _plans = plans; _planTypes = planTypes;
        _board = board; _slack = slack;
    }

    public override string Name => "mark_task_status";
    public override string Description =>
        "Set a task to a non-normal status: deferred, blocked, skipped, out_of_scope, or " +
        "needs_human_review. Bypasses the claim mechanism (override path) and requires the relevant " +
        "permission flag on the calling actor (can_mark_needs_human_review for blocked/needs_human_review, " +
        "can_override_verdict for deferred/skipped/out_of_scope). Fires board + Slack so the bound " +
        "Project V2 card moves to the matching column and the channel sees a task.blocked-style post " +
        "with the supplied reason.";

    protected override async Task<MarkTaskStatusOutput> RunAsync(MarkTaskStatusInput input, RequestContext ctx, CancellationToken ct)
    {
        // Permission gate (the state machine does this too, but failing earlier is friendlier).
        var requiredPermission = input.Status switch
        {
            TaskStatus.Blocked          => "can_mark_needs_human_review",
            TaskStatus.NeedsHumanReview => "can_mark_needs_human_review",
            TaskStatus.Deferred         => "can_override_verdict",
            TaskStatus.Skipped          => "can_override_verdict",
            TaskStatus.OutOfScope       => "can_override_verdict",
            _ => throw new WorkstreamException(WorkstreamError.Validation(
                $"mark_task_status only accepts deferred/blocked/skipped/out_of_scope/needs_human_review; got '{input.Status}'")),
        };
        if (!ctx.HasPermission(requiredPermission))
            throw new WorkstreamException(WorkstreamError.PermissionDenied(requiredPermission));

        var updated = await _tasks.OverrideStatusAsync(input.TaskId, ctx.ActorId, input.Status, input.Reason, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));

        // All these statuses map to non-normal columns (typically Blocked or Done in the
        // board mapping). Use the task.blocked Slack template since it accepts the
        // {reason} token the operator supplied.
        var plan = await _plans.GetAsync(updated.PlanId, ct).ConfigureAwait(false);
        if (plan is not null)
        {
            var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
            if (pt is not null)
            {
                var extras = new Dictionary<string, string> { ["reason"] = input.Reason };
                await Workstream.Mcp.Tools.Submission.NotificationHelpers.EnqueueBoardAndSlackAsync(
                    _board, _slack, _plans, plan, pt.Value, updated, "task.blocked", ctx, ct, extras)
                    .ConfigureAwait(false);
            }
        }

        return new MarkTaskStatusOutput(updated.Id, updated.Status);
    }
}

// ============================================================================
// record_commit
// ============================================================================

public sealed record RecordCommitInput(string EntityType, Guid EntityId, string CommitHash, Guid? RepoId = null, string? TargetBranch = null);
public sealed record RecordCommitOutput(string EntityType, Guid EntityId, string CommitHash);

public sealed class RecordCommitTool : McpTool<RecordCommitInput, RecordCommitOutput>
{
    private readonly IEventRepository _events;
    private readonly IAttemptRepository _attempts;

    public RecordCommitTool(IEventRepository events, IAttemptRepository attempts)
    {
        _events = events; _attempts = attempts;
    }

    public override string Name => "record_commit";
    public override string Description =>
        "Attach a commit hash to a task, finding, or attempt after the fact. Writes a commit_recorded " +
        "event. Use when the commit happens outside the normal submit_attempt flow (e.g. a hotfix " +
        "going in via override).";

    protected override async Task<RecordCommitOutput> RunAsync(RecordCommitInput input, RequestContext ctx, CancellationToken ct)
    {
        if (input.EntityType == EntityType.Attempt)
        {
            var updated = await _attempts.SetCommitHashAsync(input.EntityId, ctx.ActorId, input.CommitHash, ct).ConfigureAwait(false);
            if (updated is null)
                throw new WorkstreamException(WorkstreamError.NotFound("attempt"));
            return new RecordCommitOutput(input.EntityType, input.EntityId, input.CommitHash);
        }

        await _events.EmitAsync(ctx.ActorId, input.EntityType, input.EntityId, "commit_recorded",
            fromState: null, toState: null,
            payloadJson: JsonSerializer.Serialize(new { commit = input.CommitHash, repo_id = input.RepoId, branch = input.TargetBranch }),
            ct).ConfigureAwait(false);
        return new RecordCommitOutput(input.EntityType, input.EntityId, input.CommitHash);
    }
}

// ============================================================================
// create_task
// ============================================================================

public sealed record CreateTaskInput(
    Guid     PlanId,
    string   ExternalKey,
    string   Title,
    string?  Description = null,
    Guid?    PhaseId = null,
    string[]? Paths = null,
    string?  ReferencePointer = null,
    int      Priority = 0);

public sealed record CreateTaskOutput(Guid Id, string ExternalKey, string Status);

public sealed class CreateTaskTool : McpTool<CreateTaskInput, CreateTaskOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly Workstream.Core.StateMachine.IPlanTypeCache _planTypes;
    private readonly Workstream.Mcp.Notifications.IBoardSyncEnqueue _board;
    private readonly Workstream.Mcp.Notifications.ISlackNotifyEnqueue _slack;

    public CreateTaskTool(
        ITaskRepository tasks,
        IPlanRepository plans,
        Workstream.Core.StateMachine.IPlanTypeCache planTypes,
        Workstream.Mcp.Notifications.IBoardSyncEnqueue board,
        Workstream.Mcp.Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _plans = plans; _planTypes = planTypes;
        _board = board; _slack = slack;
    }

    public override string Name => "create_task";
    public override string Description =>
        "Create a new task on a plan, in pending status. The task immediately fires a task.created " +
        "notification: enqueues a board-sync row so the BoardSyncWorker creates a draft item on the " +
        "bound GitHub Projects V2 board (Backlog column) on first drain, and posts a Slack message if " +
        "the project has Slack configured. Used by orchestrators that decompose larger work units into " +
        "tasks at runtime, and by admin tools that import work from external sources.";

    protected override async Task<CreateTaskOutput> RunAsync(CreateTaskInput input, RequestContext ctx, CancellationToken ct)
    {
        var task = await _tasks.InsertAsync(new WorkTaskInsert(
            input.PlanId, input.PhaseId, input.ExternalKey, input.Title, input.Description,
            input.Paths, input.ReferencePointer, input.Priority), ct).ConfigureAwait(false);

        // Fan out so the new task immediately exists on the board (lazy-create on first
        // worker drain) and gets announced in Slack. Best-effort: if the plan or plan-type
        // can't be resolved, we still return the task — admin/import flows shouldn't fail
        // because of a misconfigured downstream.
        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false);
        if (plan is not null)
        {
            var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
            if (pt is not null)
            {
                await Workstream.Mcp.Tools.Submission.NotificationHelpers
                    .EnqueueBoardAndSlackAsync(_board, _slack, _plans, plan, pt.Value, task, "task.created", ctx, ct)
                    .ConfigureAwait(false);
            }
        }

        return new CreateTaskOutput(task.Id, task.ExternalKey, task.Status);
    }
}

// ============================================================================
// override_verdict
// ============================================================================

public sealed record OverrideVerdictInput(string EntityType, Guid EntityId, string NewStatus, string Reason);
public sealed record OverrideVerdictOutput(string EntityType, Guid EntityId, string NewStatus);

public sealed class OverrideVerdictTool : McpTool<OverrideVerdictInput, OverrideVerdictOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly Workstream.Core.StateMachine.IPlanTypeCache _planTypes;
    private readonly Workstream.Mcp.Notifications.IBoardSyncEnqueue _board;
    private readonly Workstream.Mcp.Notifications.ISlackNotifyEnqueue _slack;

    public OverrideVerdictTool(
        ITaskRepository tasks,
        IPlanRepository plans,
        Workstream.Core.StateMachine.IPlanTypeCache planTypes,
        Workstream.Mcp.Notifications.IBoardSyncEnqueue board,
        Workstream.Mcp.Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _plans = plans; _planTypes = planTypes;
        _board = board; _slack = slack;
    }

    public override string Name => "override_verdict";
    public override string Description =>
        "Force a state change on a task without holding its claim. Requires can_override_verdict on " +
        "the calling actor. Writes an override event with the supplied reason, and fires board + Slack " +
        "so the bound Project V2 card and the channel reflect the new status. The only legal way to " +
        "mutate a row without holding its claim.";

    protected override async Task<OverrideVerdictOutput> RunAsync(OverrideVerdictInput input, RequestContext ctx, CancellationToken ct)
    {
        if (!ctx.CanOverrideVerdict)
            throw new WorkstreamException(WorkstreamError.PermissionDenied("can_override_verdict"));

        if (input.EntityType != EntityType.Task)
            throw new WorkstreamException(WorkstreamError.Validation("override_verdict supports task entities in v1"));

        var updated = await _tasks.OverrideStatusAsync(input.EntityId, ctx.ActorId, input.NewStatus, input.Reason, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));

        // Resolve a Slack notification type from the new status. The board sync uses the
        // board-column mapping for the actual column flip.
        var slackType = MapStatusToNotificationType(updated.Status);
        var plan = await _plans.GetAsync(updated.PlanId, ct).ConfigureAwait(false);
        if (plan is not null)
        {
            var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
            if (pt is not null)
            {
                var extras = new Dictionary<string, string> { ["reason"] = input.Reason };
                await Workstream.Mcp.Tools.Submission.NotificationHelpers.EnqueueBoardAndSlackAsync(
                    _board, _slack, _plans, plan, pt.Value, updated, slackType, ctx, ct, extras)
                    .ConfigureAwait(false);
            }
        }

        return new OverrideVerdictOutput(EntityType.Task, updated.Id, updated.Status);
    }

    private static string MapStatusToNotificationType(string status) => status switch
    {
        TaskStatus.InProgress       => "task.in_progress",
        TaskStatus.Review           => "task.review",
        TaskStatus.Done             => "task.done",
        TaskStatus.Pending          => "task.created",
        TaskStatus.Claimed          => "task.claimed",
        _                           => "task.blocked",   // deferred / blocked / skipped / out_of_scope / needs_human_review
    };
}
