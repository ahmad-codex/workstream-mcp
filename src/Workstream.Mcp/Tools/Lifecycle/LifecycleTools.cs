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
                // needs_human_review gets its own template; everything else (blocked /
                // deferred / skipped / out_of_scope) uses the blocked template.
                var slackType = updated.Status == TaskStatus.NeedsHumanReview
                    ? "task.needs_human_review"
                    : "task.blocked";
                var extras = new Dictionary<string, string>
                {
                    ["reason"] = Workstream.Mcp.Tools.Submission.NotificationHelpers.TruncateReason(input.Reason),
                };
                await Workstream.Mcp.Tools.Submission.NotificationHelpers.EnqueueBoardAndSlackAsync(
                    _board, _slack, _plans, plan, pt.Value, updated, slackType, ctx, ct, extras, notifySlack: true)
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
    int      Priority = 0,
    Guid[]?  DependsOn = null,
    bool     NotifySlack = false);

public sealed record CreateTaskOutput(Guid Id, string ExternalKey, string Status, Guid[]? DependsOn = null);

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
        "Create a new task on a plan, in pending status. Optional 'depends_on' is a list of task ids " +
        "this new task depends on — claim_next_task and claim_specific_task both skip tasks whose " +
        "predecessors are not in a terminal state (done / deferred / skipped / out_of_scope), so the " +
        "orchestrator can parallelize unblocked work and sequence blocked work. All predecessors must " +
        "live on the same plan; cycles are rejected at insert. The board sync always fires so the " +
        "BoardSyncWorker creates a draft item on the bound GitHub Projects V2 board (Backlog column) " +
        "on first drain. Slack is opt-in: 'notify_slack' defaults to false to keep the channel quiet " +
        "during plan setup and admin imports; pass true to post a task.created message if the project " +
        "has Slack configured. Used by orchestrators that decompose larger work units into tasks at " +
        "runtime, and by admin tools that import work from external sources.";

    protected override async Task<CreateTaskOutput> RunAsync(CreateTaskInput input, RequestContext ctx, CancellationToken ct)
    {
        // Resolve the plan up front so an archived (disabled) plan is rejected before we
        // insert anything — an archived plan accepts no new tasks.
        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false);
        if (plan is not null)
            PlanGuards.EnsureNotArchived(plan);

        WorkTask task;
        try
        {
            task = await _tasks.InsertAsync(new WorkTaskInsert(
                input.PlanId, input.PhaseId, input.ExternalKey, input.Title, input.Description,
                input.Paths, input.ReferencePointer, input.Priority, input.DependsOn), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("cycle"))
        {
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.DependencyCycle, ex.Message));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("depends_on"))
        {
            throw new WorkstreamException(WorkstreamError.Validation(ex.Message));
        }

        // Fan out so the new task immediately exists on the board (lazy-create on first
        // worker drain) and, when notify_slack is true (the default), announce in Slack.
        // Best-effort: if the plan-type can't be resolved, we still return the task —
        // admin/import flows shouldn't fail because of a misconfigured downstream.
        if (plan is not null)
        {
            var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
            if (pt is not null)
            {
                await Workstream.Mcp.Tools.Submission.NotificationHelpers
                    .EnqueueBoardAndSlackAsync(_board, _slack, _plans, plan, pt.Value, task, "task.created", ctx, ct,
                        notifySlack: input.NotifySlack)
                    .ConfigureAwait(false);
            }
        }

        return new CreateTaskOutput(task.Id, task.ExternalKey, task.Status, input.DependsOn);
    }
}

// ============================================================================
// create_tasks — bulk insert with intra-batch dependency resolution by external_key
// ============================================================================

public sealed record CreateTasksTaskInput(
    string   ExternalKey,
    string   Title,
    string?  Description = null,
    Guid?    PhaseId = null,
    string[]? Paths = null,
    string?  ReferencePointer = null,
    int      Priority = 0,
    Guid[]?  DependsOnIds = null,
    string[]? DependsOnExternalKeys = null);

public sealed record CreateTasksInput(Guid PlanId, IReadOnlyList<CreateTasksTaskInput> Tasks, bool NotifySlack = false);
public sealed record CreateTasksOutput(IReadOnlyList<CreateTaskOutput> Tasks);

public sealed class CreateTasksTool : McpTool<CreateTasksInput, CreateTasksOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly Workstream.Core.StateMachine.IPlanTypeCache _planTypes;
    private readonly Workstream.Mcp.Notifications.IBoardSyncEnqueue _board;
    private readonly Workstream.Mcp.Notifications.ISlackNotifyEnqueue _slack;

    public CreateTasksTool(
        ITaskRepository tasks,
        IPlanRepository plans,
        Workstream.Core.StateMachine.IPlanTypeCache planTypes,
        Workstream.Mcp.Notifications.IBoardSyncEnqueue board,
        Workstream.Mcp.Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _plans = plans; _planTypes = planTypes;
        _board = board; _slack = slack;
    }

    public override string Name => "create_tasks";
    public override string Description =>
        "Bulk task creation for orchestrator bootstrap (e.g. Phase 0 of an audit). Accepts a list of " +
        "tasks on a single plan. Each item may carry 'depends_on_ids' (uuid list referencing tasks " +
        "already in the database) AND/OR 'depends_on_external_keys' (string list referencing other " +
        "items in this same batch by their external_key — resolved to ids after the first pass). The " +
        "batch is processed in two passes: first every task is inserted with no dependencies, then " +
        "edges are wired up using the resolved id map. Cycles are rejected — the call returns the " +
        "first cycle's task and rolls back nothing (already-inserted tasks remain, so the caller can " +
        "fix the bad edge and re-run idempotently using unique external_keys). Board sync always " +
        "fires for every inserted task so the Project V2 board reflects the batch. Slack is opt-in: " +
        "'notify_slack' defaults to false to avoid flooding the channel with a task.created burst — " +
        "pass true if you want every task announced.";

    protected override async Task<CreateTasksOutput> RunAsync(CreateTasksInput input, RequestContext ctx, CancellationToken ct)
    {
        if (input.Tasks.Count == 0)
            return new CreateTasksOutput(Array.Empty<CreateTaskOutput>());

        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        PlanGuards.EnsureNotArchived(plan);
        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);

        // Pass 1: insert every task without dependencies. Capture the id map keyed by
        // external_key so pass 2 can resolve forward references.
        var inserted = new List<WorkTask>(input.Tasks.Count);
        var byKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var item in input.Tasks)
        {
            var task = await _tasks.InsertAsync(new WorkTaskInsert(
                input.PlanId, item.PhaseId, item.ExternalKey, item.Title, item.Description,
                item.Paths, item.ReferencePointer, item.Priority, DependsOn: null), ct).ConfigureAwait(false);
            inserted.Add(task);
            byKey[item.ExternalKey] = task.Id;
        }

        // Pass 2: wire up dependencies. Each item's edges are inserted via a second
        // InsertAsync call would re-insert the task — instead, write edges directly
        // through the repository's dependency-only path. We don't have one yet; for the
        // first cut, fall back to a tiny inline insert + cycle check that mirrors the
        // single-task path.
        var outputs = new List<CreateTaskOutput>(input.Tasks.Count);
        for (var i = 0; i < input.Tasks.Count; i++)
        {
            var item = input.Tasks[i];
            var taskId = inserted[i].Id;
            var edges = new List<Guid>();
            if (item.DependsOnIds is { Length: > 0 } ids)
                edges.AddRange(ids);
            if (item.DependsOnExternalKeys is { Length: > 0 } keys)
            {
                foreach (var k in keys)
                {
                    if (!byKey.TryGetValue(k, out var depId))
                        throw new WorkstreamException(WorkstreamError.Validation(
                            $"depends_on_external_keys references unknown key '{k}' for task '{item.ExternalKey}'"));
                    edges.Add(depId);
                }
            }
            var distinct = edges.Where(e => e != taskId).Distinct().ToArray();
            if (distinct.Length > 0)
            {
                try
                {
                    await _tasks.WireDependenciesAsync(taskId, plan.Id, distinct, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("cycle"))
                {
                    throw new WorkstreamException(new WorkstreamError(ErrorCodes.DependencyCycle,
                        $"dependency cycle detected at task '{item.ExternalKey}'"));
                }
                catch (InvalidOperationException ex)
                {
                    throw new WorkstreamException(WorkstreamError.Validation(ex.Message));
                }
            }

            if (pt is not null)
            {
                await Workstream.Mcp.Tools.Submission.NotificationHelpers
                    .EnqueueBoardAndSlackAsync(_board, _slack, _plans, plan, pt.Value, inserted[i], "task.created", ctx, ct,
                        notifySlack: input.NotifySlack)
                    .ConfigureAwait(false);
            }
            outputs.Add(new CreateTaskOutput(taskId, item.ExternalKey, inserted[i].Status, distinct.Length == 0 ? null : distinct));
        }
        return new CreateTasksOutput(outputs);
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
    private readonly IFindingRepository _findings;
    private readonly IPlanRepository _plans;
    private readonly Workstream.Core.StateMachine.IPlanTypeCache _planTypes;
    private readonly Workstream.Mcp.Notifications.IBoardSyncEnqueue _board;
    private readonly Workstream.Mcp.Notifications.ISlackNotifyEnqueue _slack;

    public OverrideVerdictTool(
        ITaskRepository tasks,
        IFindingRepository findings,
        IPlanRepository plans,
        Workstream.Core.StateMachine.IPlanTypeCache planTypes,
        Workstream.Mcp.Notifications.IBoardSyncEnqueue board,
        Workstream.Mcp.Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _findings = findings; _plans = plans; _planTypes = planTypes;
        _board = board; _slack = slack;
    }

    public override string Name => "override_verdict";
    public override string Description =>
        "Force a state change on a task or finding without holding its claim. Requires " +
        "can_override_verdict on the calling actor. Writes an override event with the supplied " +
        "reason, and (for tasks) fires board + Slack so the bound Project V2 card and the channel " +
        "reflect the new status. Use when a subagent's claim has gone stale but the work it " +
        "produced needs to be recorded (e.g. a verifier that finished after its TTL expired). " +
        "For attempts, use record_commit to attach commit metadata; an attempt has no status of " +
        "its own — the parent finding carries the verdict, so override the finding instead.";

    protected override async Task<OverrideVerdictOutput> RunAsync(OverrideVerdictInput input, RequestContext ctx, CancellationToken ct)
    {
        if (!ctx.CanOverrideVerdict)
            throw new WorkstreamException(WorkstreamError.PermissionDenied("can_override_verdict"));

        return input.EntityType switch
        {
            EntityType.Task     => await OverrideTaskAsync(input, ctx, ct).ConfigureAwait(false),
            EntityType.Finding  => await OverrideFindingAsync(input, ctx, ct).ConfigureAwait(false),
            EntityType.Attempt  => throw new WorkstreamException(WorkstreamError.Validation(
                "override_verdict does not apply to attempts directly — an attempt has no status of " +
                "its own. To record commit metadata on an attempt that bypassed the normal flow, use " +
                "record_commit. To force a finding-level verdict (fixed / fix_failed / partial / " +
                "needs_human_review) when the fix-verifier's claim went stale, call override_verdict " +
                "on the parent finding with the desired status.")),
            _ => throw new WorkstreamException(WorkstreamError.Validation(
                $"override_verdict only accepts entity_type in {{task, finding}}; got '{input.EntityType}'")),
        };
    }

    private async Task<OverrideVerdictOutput> OverrideTaskAsync(OverrideVerdictInput input, RequestContext ctx, CancellationToken ct)
    {
        var updated = await _tasks.OverrideStatusAsync(input.EntityId, ctx.ActorId, input.NewStatus, input.Reason, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));

        // Resolve a Slack notification type from the new status. The board sync uses the
        // board-column mapping for the actual column flip.
        var slackType = MapTaskStatusToNotificationType(updated.Status);
        var plan = await _plans.GetAsync(updated.PlanId, ct).ConfigureAwait(false);
        if (plan is not null)
        {
            var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
            if (pt is not null)
            {
                var extras = new Dictionary<string, string>
                {
                    ["reason"] = Workstream.Mcp.Tools.Submission.NotificationHelpers.TruncateReason(input.Reason),
                };
                await Workstream.Mcp.Tools.Submission.NotificationHelpers.EnqueueBoardAndSlackAsync(
                    _board, _slack, _plans, plan, pt.Value, updated, slackType, ctx, ct, extras, notifySlack: true)
                    .ConfigureAwait(false);
            }
        }

        return new OverrideVerdictOutput(EntityType.Task, updated.Id, updated.Status);
    }

    private async Task<OverrideVerdictOutput> OverrideFindingAsync(OverrideVerdictInput input, RequestContext ctx, CancellationToken ct)
    {
        var updated = await _findings.OverrideStatusAsync(input.EntityId, ctx.ActorId, input.NewStatus, input.Reason, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.NotFound("finding"));

        // Findings don't sync to a board card directly — the parent task already shows on
        // the board. We DO post Slack for findings because operators care about confirmed /
        // rejected / fixed transitions, especially when they happen via override (which means
        // a human is recovering from a stuck-claim or escalating a stale verifier result).
        var task = await _tasks.GetAsync(updated.TaskId, ct).ConfigureAwait(false);
        if (task is not null)
        {
            var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false);
            if (plan is not null)
            {
                var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
                if (pt is not null)
                {
                    var slackType = MapFindingStatusToNotificationType(updated.Status);
                    var extras = new Dictionary<string, string>
                    {
                        ["reason"] = Workstream.Mcp.Tools.Submission.NotificationHelpers.TruncateReason(input.Reason),
                        ["finding_key"] = updated.ExternalKey,
                        ["severity"] = updated.Severity ?? "unknown",
                    };
                    await Workstream.Mcp.Tools.Submission.NotificationHelpers
                        .EnqueueFindingSlackAsync(_slack, _plans, plan, pt.Value, updated, updated.TaskId, slackType, ctx, ct, extras)
                        .ConfigureAwait(false);
                    // Refresh the parent task card so the audit ledger reflects the override.
                    await Workstream.Mcp.Tools.Submission.NotificationHelpers
                        .EnqueueBoardRefreshAsync(_board, plan, pt.Value, task, ct).ConfigureAwait(false);
                }
            }
        }

        return new OverrideVerdictOutput(EntityType.Finding, updated.Id, updated.Status);
    }

    private static string MapTaskStatusToNotificationType(string status) => status switch
    {
        TaskStatus.InProgress       => "task.in_progress",
        TaskStatus.Review           => "task.review",
        TaskStatus.Done             => "task.done",
        TaskStatus.Pending          => "task.created",
        TaskStatus.Claimed          => "task.claimed",
        TaskStatus.NeedsHumanReview => "task.needs_human_review",
        _                           => "task.blocked",   // deferred / blocked / skipped / out_of_scope
    };

    private static string MapFindingStatusToNotificationType(string status) => status switch
    {
        "confirmed"           => "finding.confirmed",
        "rejected"            => "finding.rejected",
        "ambiguous"           => "finding.ambiguous",
        "fixed"               => "fix.confirmed",
        "fix_failed"          => "fix.failed",
        "partial"             => "fix.partial",
        "needs_human_review"  => "finding.needs_human_review",
        "deferred"            => "finding.deferred",
        _                     => "finding.confirmed",
    };
}
