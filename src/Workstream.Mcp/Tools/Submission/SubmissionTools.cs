using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Core.StateMachine;
using Workstream.Data;
using Workstream.Data.Repositories;
using TaskStatus = Workstream.Core.Domain.TaskStatus;

namespace Workstream.Mcp.Tools.Submission;

// ============================================================================
// start_work — moves a task from 'claimed' to 'in_progress' and flips the board column.
// ============================================================================

public sealed record StartWorkInput(Guid ClaimToken);
public sealed record StartWorkOutput(Guid TaskId, string Status);

public sealed class StartWorkTool : McpTool<StartWorkInput, StartWorkOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly StateMachineService _sm;
    private readonly Notifications.IBoardSyncEnqueue _boardSync;
    private readonly Notifications.ISlackNotifyEnqueue _slack;

    public StartWorkTool(
        ITaskRepository tasks, IPlanRepository plans, IPlanTypeCache planTypes,
        StateMachineService sm,
        Notifications.IBoardSyncEnqueue boardSync, Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _plans = plans; _planTypes = planTypes;
        _sm = sm;
        _boardSync = boardSync; _slack = slack;
    }

    public override string Name => "start_work";
    public override string Description =>
        "Transition a claimed task from 'claimed' to 'in_progress'. Triggers the board sync to move " +
        "the card to In Progress. Requires the original claim token; a stale token yields stale_claim.";

    protected override async Task<StartWorkOutput> RunAsync(StartWorkInput input, RequestContext ctx, CancellationToken ct)
    {
        // Resolve current task and plan type for state-machine validation.
        var taskByToken = await TaskRepoHelpers.GetByClaimAsync(_tasks, input.ClaimToken, ct).ConfigureAwait(false)
                          ?? throw new WorkstreamException(WorkstreamError.StaleClaim());
        var plan = await _plans.GetAsync(taskByToken.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                 ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));

        var transition = _sm.ValidateTransition(pt.Graph, EntityType.Task,
            from: taskByToken.Status, to: TaskStatus.InProgress, via: "start_work",
            actor: ctx, claimRole: taskByToken.Claim.Role, context: TransitionContext.Empty);
        if (!transition.Allowed)
            throw new WorkstreamException(new WorkstreamError(transition.RejectionCode ?? ErrorCodes.IllegalTransition,
                transition.RejectionMessage ?? "illegal transition",
                transition.AllowedNext is null ? null
                    : new Dictionary<string, object?> { ["allowed_next"] = transition.AllowedNext }));

        var updated = await _tasks.UpdateStatusWithClaimAsync(
            input.ClaimToken, ctx.ActorId, TaskStatus.InProgress, clearClaim: false,
            eventType: "started_work", eventPayloadJson: null, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.StaleClaim());

        await NotificationHelpers.EnqueueBoardAndSlackAsync(
            _boardSync, _slack, _plans, plan, pt, updated, "task.in_progress", ctx, ct,
            assigneeGithubUsername: ctx.GithubUsername, notifySlack: true).ConfigureAwait(false);

        return new StartWorkOutput(updated.Id, updated.Status);
    }
}

// ============================================================================
// submit_findings — audit-plan only
// ============================================================================

public sealed record SubmitFindingsInput(Guid ClaimToken, IReadOnlyList<FindingInput> Findings);
public sealed record SubmitFindingsOutput(string TaskStatus, IReadOnlyList<Guid> FindingIds);

public sealed class SubmitFindingsTool : McpTool<SubmitFindingsInput, SubmitFindingsOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IFindingRepository _findings;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly StateMachineService _sm;
    private readonly Notifications.IBoardSyncEnqueue _boardSync;
    private readonly Notifications.ISlackNotifyEnqueue _slack;

    public SubmitFindingsTool(
        ITaskRepository tasks, IFindingRepository findings, IPlanRepository plans, IPlanTypeCache planTypes,
        StateMachineService sm, Notifications.IBoardSyncEnqueue boardSync, Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _findings = findings; _plans = plans; _planTypes = planTypes;
        _sm = sm; _boardSync = boardSync; _slack = slack;
    }

    public override string Name => "submit_findings";
    public override string Description =>
        "Audit-plan only: submit zero or more findings for the currently-claimed task. " +
        "If the findings list is empty, the task transitions directly to done (a clean audit). " +
        "If non-empty, the task transitions to review and each finding is created in pending_verification.";

    protected override async Task<SubmitFindingsOutput> RunAsync(SubmitFindingsInput input, RequestContext ctx, CancellationToken ct)
    {
        var task = await TaskRepoHelpers.GetByClaimAsync(_tasks, input.ClaimToken, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.StaleClaim());
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                 ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));

        var targetStatus = input.Findings.Count == 0 ? TaskStatus.Done : TaskStatus.Review;
        var transition = _sm.ValidateTransition(pt.Graph, EntityType.Task,
            from: task.Status, to: targetStatus, via: "submit_findings",
            actor: ctx, claimRole: task.Claim.Role,
            context: new TransitionContext(ProposedFindingCount: input.Findings.Count));
        if (!transition.Allowed)
            throw new WorkstreamException(new WorkstreamError(transition.RejectionCode ?? ErrorCodes.IllegalTransition,
                transition.RejectionMessage ?? "illegal transition",
                transition.AllowedNext is null ? null : new Dictionary<string, object?> { ["allowed_next"] = transition.AllowedNext }));

        var inserted = await _findings.InsertManyAsync(task.Id, input.Findings, ct).ConfigureAwait(false);

        var clearClaim = targetStatus == TaskStatus.Done;
        var updated = await _tasks.UpdateStatusWithClaimAsync(
            input.ClaimToken, ctx.ActorId, targetStatus, clearClaim,
            eventType: "findings_submitted",
            eventPayloadJson: JsonSerializer.Serialize(new { count = input.Findings.Count }), ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.StaleClaim());

        var findingsExtras = new Dictionary<string, string>
        {
            ["finding_count"] = input.Findings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await NotificationHelpers.EnqueueBoardAndSlackAsync(_boardSync, _slack, _plans, plan, pt, updated,
            updated.Status == TaskStatus.Done ? "task.done" : "task.review", ctx, ct,
            extraTokens: findingsExtras, notifySlack: true).ConfigureAwait(false);

        return new SubmitFindingsOutput(updated.Status, inserted.Select(f => f.Id).ToList());
    }
}

// ============================================================================
// submit_verification_verdict — audit-plan only
// ============================================================================

public sealed record SubmitVerificationVerdictInput(Guid ClaimToken, string Verdict, VerdictEvidence Evidence);
public sealed record SubmitVerificationVerdictOutput(Guid FindingId, string Status, Guid VerdictId);

public sealed class SubmitVerificationVerdictTool : McpTool<SubmitVerificationVerdictInput, SubmitVerificationVerdictOutput>
{
    private readonly IFindingRepository _findings;
    private readonly IVerdictRepository _verdicts;
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly StateMachineService _sm;
    private readonly Notifications.ISlackNotifyEnqueue _slack;
    private readonly Notifications.IBoardSyncEnqueue _boardSync;

    public SubmitVerificationVerdictTool(IFindingRepository findings, IVerdictRepository verdicts,
        ITaskRepository tasks, IPlanRepository plans, IPlanTypeCache planTypes,
        StateMachineService sm, Notifications.ISlackNotifyEnqueue slack, Notifications.IBoardSyncEnqueue boardSync)
    {
        _findings = findings; _verdicts = verdicts; _tasks = tasks; _plans = plans; _planTypes = planTypes;
        _sm = sm; _slack = slack; _boardSync = boardSync;
    }

    public override string Name => "submit_verification_verdict";
    public override string Description =>
        "Audit-plan only: submit the verifier's verdict on a pending-verification finding. " +
        "verdict ∈ {confirmed, rejected, ambiguous}. Evidence (pre_output, post_output, etc.) is recorded " +
        "into the verdicts table and the finding status transitions accordingly.";

    protected override async Task<SubmitVerificationVerdictOutput> RunAsync(SubmitVerificationVerdictInput input, RequestContext ctx, CancellationToken ct)
    {
        var finding = await FindingRepoHelpers.GetByClaimAsync(_findings, input.ClaimToken, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.StaleClaim());
        var task = await _tasks.GetAsync(finding.TaskId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                 ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));

        var targetStatus = input.Verdict switch
        {
            VerdictType.Confirmed => FindingStatus.Confirmed,
            VerdictType.Rejected  => FindingStatus.Rejected,
            VerdictType.Ambiguous => FindingStatus.Ambiguous,
            _ => throw new WorkstreamException(WorkstreamError.Validation($"unknown verdict '{input.Verdict}'")),
        };

        var transition = _sm.ValidateTransition(pt.Graph, EntityType.Finding,
            from: finding.Status, to: targetStatus, via: "submit_verification_verdict",
            actor: ctx, claimRole: finding.Claim.Role,
            context: new TransitionContext(Verdict: input.Verdict));
        if (!transition.Allowed)
            throw new WorkstreamException(new WorkstreamError(transition.RejectionCode ?? ErrorCodes.IllegalTransition,
                transition.RejectionMessage ?? "illegal transition",
                transition.AllowedNext is null ? null : new Dictionary<string, object?> { ["allowed_next"] = transition.AllowedNext }));

        var verdict = await _verdicts.InsertForFindingAsync(finding.Id, ctx.ActorId, input.Verdict, input.Evidence, ct).ConfigureAwait(false);

        var updated = await _findings.UpdateStatusWithClaimAsync(
            input.ClaimToken, ctx.ActorId, targetStatus, clearClaim: true,
            eventType: "verdict_submitted",
            eventPayloadJson: JsonSerializer.Serialize(new { verdict = input.Verdict, verdict_id = verdict.Id }),
            ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.StaleClaim());

        // One notification type per verdict — an ambiguous verdict is no longer
        // mislabelled "rejected". The verifier's reason_category surfaces as {reason}.
        var verdictSlackType = input.Verdict switch
        {
            VerdictType.Confirmed => "finding.confirmed",
            VerdictType.Rejected  => "finding.rejected",
            VerdictType.Ambiguous => "finding.ambiguous",
            _                     => "finding.rejected",
        };
        var verdictExtras = new Dictionary<string, string>();
        var verdictReason = NotificationHelpers.HumanizeReason(input.Evidence?.ReasonCategory);
        if (verdictReason is not null) verdictExtras["reason"] = verdictReason;
        await NotificationHelpers.EnqueueFindingSlackAsync(_slack, _plans, plan, pt, updated, task.Id,
            verdictSlackType, ctx, ct, verdictExtras.Count > 0 ? verdictExtras : null).ConfigureAwait(false);
        // Refresh the parent task card so the audit ledger reflects this verdict.
        await NotificationHelpers.EnqueueBoardRefreshAsync(_boardSync, plan, pt, task, ct).ConfigureAwait(false);

        return new SubmitVerificationVerdictOutput(updated.Id, updated.Status, verdict.Id);
    }
}

// ============================================================================
// submit_attempt — both plan types
// ============================================================================

public sealed record SubmitAttemptInput(Guid ClaimToken, AttemptInput Attempt);
public sealed record SubmitAttemptOutput(Guid AttemptId, int AttemptNumber, string TaskStatus);

public sealed class SubmitAttemptTool : McpTool<SubmitAttemptInput, SubmitAttemptOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IFindingRepository _findings;
    private readonly IAttemptRepository _attempts;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly StateMachineService _sm;
    private readonly Notifications.IBoardSyncEnqueue _boardSync;
    private readonly Notifications.ISlackNotifyEnqueue _slack;

    public SubmitAttemptTool(ITaskRepository tasks, IFindingRepository findings, IAttemptRepository attempts,
        IPlanRepository plans, IPlanTypeCache planTypes, StateMachineService sm,
        Notifications.IBoardSyncEnqueue boardSync, Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _findings = findings; _attempts = attempts; _plans = plans; _planTypes = planTypes;
        _sm = sm; _boardSync = boardSync; _slack = slack;
    }

    public override string Name => "submit_attempt";
    public override string Description =>
        "Submit a developer's (or fixer's) attempt at the work: list of files changed, approach summary, " +
        "side effects, build command, test scenario, diff reference. For dev-plan tasks this transitions " +
        "the task to review. For audit findings this records an attempt in the findings's attempt chain. " +
        "Returns retry_cap_exceeded if attempt count is at the plan's retry cap.";

    protected override async Task<SubmitAttemptOutput> RunAsync(SubmitAttemptInput input, RequestContext ctx, CancellationToken ct)
    {
        // The claim could be on a task (dev plan) or a finding (audit fix). Inspect.
        var task = await TaskRepoHelpers.GetByClaimAsync(_tasks, input.ClaimToken, ct).ConfigureAwait(false);
        if (task is not null)
            return await SubmitForTaskAsync(task, input, ctx, ct).ConfigureAwait(false);

        var finding = await FindingRepoHelpers.GetByClaimAsync(_findings, input.ClaimToken, ct).ConfigureAwait(false);
        if (finding is not null)
            return await SubmitForFindingAsync(finding, input, ctx, ct).ConfigureAwait(false);

        throw new WorkstreamException(WorkstreamError.StaleClaim());
    }

    private async Task<SubmitAttemptOutput> SubmitForTaskAsync(WorkTask task, SubmitAttemptInput input, RequestContext ctx, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                 ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));

        var attempts = await _attempts.CountForTaskAsync(task.Id, ct).ConfigureAwait(false);
        if (attempts >= pt.Row.RetryCap)
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.RetryCapExceeded,
                $"task already has {attempts} attempts; retry cap is {pt.Row.RetryCap}"));

        var transition = _sm.ValidateTransition(pt.Graph, EntityType.Task,
            from: task.Status, to: TaskStatus.Review, via: "submit_attempt",
            actor: ctx, claimRole: task.Claim.Role,
            context: new TransitionContext(AttemptCount: attempts + 1, RetryCap: pt.Row.RetryCap));
        if (!transition.Allowed)
            throw new WorkstreamException(new WorkstreamError(transition.RejectionCode ?? ErrorCodes.IllegalTransition,
                transition.RejectionMessage ?? "illegal transition",
                transition.AllowedNext is null ? null : new Dictionary<string, object?> { ["allowed_next"] = transition.AllowedNext }));

        var attempt = await _attempts.InsertForTaskAsync(task.Id, ctx.ActorId, input.Attempt, ct).ConfigureAwait(false);
        var updated = await _tasks.UpdateStatusWithClaimAsync(
            input.ClaimToken, ctx.ActorId, TaskStatus.Review, clearClaim: true,
            eventType: "attempt_submitted",
            eventPayloadJson: JsonSerializer.Serialize(new { attempt_id = attempt.Id, attempt_number = attempt.AttemptNumber }),
            ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.StaleClaim());

        await NotificationHelpers.EnqueueBoardAndSlackAsync(_boardSync, _slack, _plans, plan, pt, updated, "task.review", ctx, ct, notifySlack: true).ConfigureAwait(false);

        return new SubmitAttemptOutput(attempt.Id, attempt.AttemptNumber, updated.Status);
    }

    private async Task<SubmitAttemptOutput> SubmitForFindingAsync(Finding finding, SubmitAttemptInput input, RequestContext ctx, CancellationToken ct)
    {
        var task = await _tasks.GetAsync(finding.TaskId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                 ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));

        var attempts = await _attempts.CountForFindingAsync(finding.Id, ct).ConfigureAwait(false);
        if (attempts >= pt.Row.RetryCap)
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.RetryCapExceeded,
                $"finding already has {attempts} attempts; retry cap is {pt.Row.RetryCap}"));

        var attempt = await _attempts.InsertForFindingAsync(finding.Id, ctx.ActorId, input.Attempt, ct).ConfigureAwait(false);
        // The finding's claim is released on submission; it goes back to in_fix awaiting verifier.
        // Status stays in_fix; the verifier claim will move it forward.
        var updated = await _findings.UpdateStatusWithClaimAsync(
            input.ClaimToken, ctx.ActorId, FindingStatus.InFix, clearClaim: true,
            eventType: "attempt_submitted",
            eventPayloadJson: JsonSerializer.Serialize(new { attempt_id = attempt.Id, attempt_number = attempt.AttemptNumber }),
            ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.StaleClaim());
        // Refresh the parent task card so the audit ledger shows the fix attempt.
        await NotificationHelpers.EnqueueBoardRefreshAsync(_boardSync, plan, pt, task, ct).ConfigureAwait(false);
        return new SubmitAttemptOutput(attempt.Id, attempt.AttemptNumber, updated.Status);
    }
}

// ============================================================================
// submit_attempt_verdict — audit fix-verifier, dev reviewer too via submit_review_decision wrapper
// ============================================================================

public sealed record SubmitAttemptVerdictInput(
    Guid          AttemptId,
    string        Verdict,
    VerdictEvidence Evidence,
    string?       CommitHash = null);

public sealed record SubmitAttemptVerdictOutput(Guid FindingId, string FindingStatus, Guid VerdictId, string? CommitRecorded);

public sealed class SubmitAttemptVerdictTool : McpTool<SubmitAttemptVerdictInput, SubmitAttemptVerdictOutput>
{
    private readonly IAttemptRepository _attempts;
    private readonly IVerdictRepository _verdicts;
    private readonly IFindingRepository _findings;
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly StateMachineService _sm;
    private readonly Notifications.ISlackNotifyEnqueue _slack;
    private readonly Notifications.IBoardSyncEnqueue _boardSync;

    public SubmitAttemptVerdictTool(IAttemptRepository attempts, IVerdictRepository verdicts,
        IFindingRepository findings, ITaskRepository tasks, IPlanRepository plans, IPlanTypeCache planTypes,
        StateMachineService sm, Notifications.ISlackNotifyEnqueue slack, Notifications.IBoardSyncEnqueue boardSync)
    {
        _attempts = attempts; _verdicts = verdicts; _findings = findings; _tasks = tasks; _plans = plans; _planTypes = planTypes;
        _sm = sm; _slack = slack; _boardSync = boardSync;
    }

    public override string Name => "submit_attempt_verdict";
    public override string Description =>
        "Audit-plan: the fix-verifier's verdict on a fixer's attempt. verdict ∈ {fix_confirmed, " +
        "fix_failed, partial}. On fix_confirmed, commit_hash is required and the finding moves to fixed. " +
        "On fix_failed below retry cap, the finding moves to fix_failed (still claimable). On partial, the " +
        "server auto-creates a child finding for the remaining work.";

    protected override async Task<SubmitAttemptVerdictOutput> RunAsync(SubmitAttemptVerdictInput input, RequestContext ctx, CancellationToken ct)
    {
        var attempt = await _attempts.GetAsync(input.AttemptId, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.NotFound("attempt"));
        if (attempt.FindingId is null)
            throw new WorkstreamException(WorkstreamError.Validation("submit_attempt_verdict requires an attempt attached to a finding"));

        var finding = await _findings.GetAsync(attempt.FindingId.Value, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.NotFound("finding"));
        var task = await _tasks.GetAsync(finding.TaskId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                 ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));

        var attemptCount = await _attempts.CountForFindingAsync(finding.Id, ct).ConfigureAwait(false);
        var targetStatus = input.Verdict switch
        {
            VerdictType.FixConfirmed => FindingStatus.Fixed,
            VerdictType.FixFailed    => attemptCount >= pt.Row.RetryCap ? FindingStatus.NeedsHumanReview : FindingStatus.FixFailed,
            VerdictType.Partial      => FindingStatus.Partial,
            _ => throw new WorkstreamException(WorkstreamError.Validation($"unknown verdict '{input.Verdict}'")),
        };

        if (input.Verdict == VerdictType.FixConfirmed && string.IsNullOrWhiteSpace(input.CommitHash))
            throw new WorkstreamException(WorkstreamError.Validation("commit_hash is required for fix_confirmed verdicts"));

        var verdict = await _verdicts.InsertForAttemptAsync(attempt.Id, ctx.ActorId, input.Verdict, input.Evidence, ct).ConfigureAwait(false);
        string? commitRecorded = null;
        if (input.CommitHash is { Length: > 0 })
        {
            await _attempts.SetCommitHashAsync(attempt.Id, ctx.ActorId, input.CommitHash, ct).ConfigureAwait(false);
            commitRecorded = input.CommitHash;
        }

        // Move the finding to its new status. We don't use a claim token here — fix_verifier
        // doesn't necessarily hold one in v1 (claim_next_attempt_for_review is reserved for future).
        // Instead, directly mutate the row (override-style) and record an event.
        await using (var conn = await GetConnAsync().ConfigureAwait(false))
        {
            await Dapper.SqlMapper.ExecuteAsync(conn, new Dapper.CommandDefinition(
                "UPDATE findings SET status = @s WHERE id = @id",
                new { s = targetStatus, id = finding.Id }, cancellationToken: ct)).ConfigureAwait(false);
        }

        var slackType = input.Verdict switch
        {
            VerdictType.FixConfirmed => "fix.confirmed",
            VerdictType.FixFailed    => "fix.failed",
            VerdictType.Partial      => "fix.partial",
            _                        => "fix.failed",
        };
        var fixExtras = new Dictionary<string, string>
        {
            ["attempt"] = $"{attemptCount}/{pt.Row.RetryCap}",
        };
        if (commitRecorded is { Length: > 0 })
            fixExtras["commit"] = NotificationHelpers.ShortHash(commitRecorded);
        var fixReason = NotificationHelpers.HumanizeReason(input.Evidence?.ReasonCategory);
        if (fixReason is not null) fixExtras["reason"] = fixReason;
        await NotificationHelpers.EnqueueFindingSlackAsync(_slack, _plans, plan, pt,
            finding with { Status = targetStatus }, task.Id, slackType, ctx, ct, fixExtras).ConfigureAwait(false);
        // Refresh the parent task card so the audit ledger reflects this fix verdict.
        await NotificationHelpers.EnqueueBoardRefreshAsync(_boardSync, plan, pt, task, ct).ConfigureAwait(false);

        return new SubmitAttemptVerdictOutput(finding.Id, targetStatus, verdict.Id, commitRecorded);
    }

    // Local helper since this tool currently directly opens a connection for one cleanup step.
    // The cleaner option is to push this into FindingRepository.OverrideStatusAsync; left as a
    // tracking item.
    private async Task<Npgsql.NpgsqlConnection> GetConnAsync()
    {
        var f = (IDbConnectionFactory)_findings.GetType().GetField("_factory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_findings)!;
        return await f.OpenAsync().ConfigureAwait(false);
    }
}

// ============================================================================
// submit_review_decision — dev-plan convenience
// ============================================================================

public sealed record SubmitReviewDecisionInput(Guid TaskId, string Decision, string? Comments = null, string? CommitHash = null);
public sealed record SubmitReviewDecisionOutput(Guid TaskId, string Status);

public sealed class SubmitReviewDecisionTool : McpTool<SubmitReviewDecisionInput, SubmitReviewDecisionOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IAttemptRepository _attempts;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly StateMachineService _sm;
    private readonly IEventRepository _events;
    private readonly Notifications.IBoardSyncEnqueue _boardSync;
    private readonly Notifications.ISlackNotifyEnqueue _slack;

    public SubmitReviewDecisionTool(ITaskRepository tasks, IAttemptRepository attempts, IPlanRepository plans,
        IPlanTypeCache planTypes, StateMachineService sm, IEventRepository events,
        Notifications.IBoardSyncEnqueue boardSync, Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _attempts = attempts; _plans = plans; _planTypes = planTypes;
        _sm = sm; _events = events;
        _boardSync = boardSync; _slack = slack;
    }

    public override string Name => "submit_review_decision";
    public override string Description =>
        "Dev-plan: a reviewer's verdict on a task in review. decision ∈ {approved, changes_requested}. " +
        "On approved, the task moves to done and commit_hash (if provided) is recorded on the latest attempt. " +
        "On changes_requested below retry cap, the task returns to in_progress for another developer pass; " +
        "at cap, it moves to needs_human_review.";

    protected override async Task<SubmitReviewDecisionOutput> RunAsync(SubmitReviewDecisionInput input, RequestContext ctx, CancellationToken ct)
    {
        var task = await _tasks.GetAsync(input.TaskId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                 ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));

        var attemptCount = await _attempts.CountForTaskAsync(task.Id, ct).ConfigureAwait(false);
        var targetStatus = input.Decision switch
        {
            VerdictType.Approved         => TaskStatus.Done,
            VerdictType.ChangesRequested => attemptCount >= pt.Row.RetryCap ? TaskStatus.NeedsHumanReview : TaskStatus.InProgress,
            _ => throw new WorkstreamException(WorkstreamError.Validation($"unknown decision '{input.Decision}'")),
        };

        var transition = _sm.ValidateTransition(pt.Graph, EntityType.Task,
            from: task.Status, to: targetStatus, via: "submit_review_decision",
            actor: ctx, claimRole: "reviewer",
            context: new TransitionContext(Verdict: input.Decision, AttemptCount: attemptCount, RetryCap: pt.Row.RetryCap));
        if (!transition.Allowed)
            throw new WorkstreamException(new WorkstreamError(transition.RejectionCode ?? ErrorCodes.IllegalTransition,
                transition.RejectionMessage ?? "illegal transition",
                transition.AllowedNext is null ? null : new Dictionary<string, object?> { ["allowed_next"] = transition.AllowedNext }));

        // Reviewers don't hold a task claim, so use override path.
        var updated = await _tasks.OverrideStatusAsync(task.Id, ctx.ActorId, targetStatus,
            input.Comments ?? input.Decision, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));

        await _events.EmitAsync(ctx.ActorId, EntityType.Task, task.Id, "review_decision",
            task.Status, targetStatus,
            JsonSerializer.Serialize(new { decision = input.Decision, comments = input.Comments, commit = input.CommitHash }),
            ct).ConfigureAwait(false);

        if (input.CommitHash is { Length: > 0 })
        {
            var attempts = await _attempts.ListForTaskAsync(task.Id, ct).ConfigureAwait(false);
            if (attempts.Count > 0)
            {
                await _attempts.SetCommitHashAsync(attempts[^1].Id, ctx.ActorId, input.CommitHash, ct).ConfigureAwait(false);
            }
        }

        var slackType = targetStatus switch
        {
            TaskStatus.Done       => "task.done",
            TaskStatus.InProgress => "task.in_progress",
            _ => "task.blocked",
        };

        // Surface review context in the Slack body via template tokens. For task.done
        // we expose commit hash, reviewer (the calling actor), decision, comments, and
        // the total attempt count. Templates pick what to show.
        var extras = new Dictionary<string, string>
        {
            ["commit"]   = input.CommitHash ?? "",
            ["reviewer"] = ctx.DisplayActor,
            ["decision"] = input.Decision,
            ["comments"] = input.Comments ?? "",
            ["attempts"] = attemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await NotificationHelpers.EnqueueBoardAndSlackAsync(
            _boardSync, _slack, _plans, plan, pt, updated, slackType, ctx, ct, extras, notifySlack: true).ConfigureAwait(false);

        return new SubmitReviewDecisionOutput(updated.Id, updated.Status);
    }
}

// ============================================================================
// Shared helpers
// ============================================================================

internal static class TaskRepoHelpers
{
    public static async Task<WorkTask?> GetByClaimAsync(ITaskRepository repo, Guid claimToken, CancellationToken ct)
    {
        // ITaskRepository doesn't have a GetByClaim helper; we route through a small SQL via the
        // factory we can grab from the repository via the same reflection trick used elsewhere.
        var factoryField = repo.GetType().GetField("_factory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (factoryField?.GetValue(repo) is not IDbConnectionFactory factory)
            return null;
        await using var conn = await factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = """
            SELECT id AS Id, plan_id AS PlanId, phase_id AS PhaseId, external_key AS ExternalKey,
                   title AS Title, description AS Description, paths AS Paths,
                   reference_pointer AS ReferencePointer, priority AS Priority, status AS Status,
                   assignee_actor_id AS AssigneeActorId, github_board_item_id AS GithubBoardItemId,
                   github_issue_number AS GithubIssueNumber, github_issue_node_id AS GithubIssueNodeId,
                   claim_actor_id AS ClaimActorId, claim_role AS ClaimRole, claim_token AS ClaimToken,
                   claimed_at AS ClaimedAt, claimed_until AS ClaimedUntil,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM tasks WHERE claim_token = @claimToken
            """;
        var row = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<TaskRowSnapshot>(
            conn, new Dapper.CommandDefinition(sql, new { claimToken }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDomain();
    }

    internal sealed record TaskRowSnapshot
    {
        public Guid    Id                  { get; init; }
        public Guid    PlanId              { get; init; }
        public Guid?   PhaseId             { get; init; }
        public string  ExternalKey         { get; init; } = "";
        public string  Title               { get; init; } = "";
        public string? Description         { get; init; }
        public string[]? Paths             { get; init; }
        public string? ReferencePointer    { get; init; }
        public int     Priority            { get; init; }
        public string  Status              { get; init; } = "";
        public Guid?   AssigneeActorId     { get; init; }
        public string? GithubBoardItemId   { get; init; }
        public int?    GithubIssueNumber   { get; init; }
        public string? GithubIssueNodeId   { get; init; }
        public Guid?   ClaimActorId        { get; init; }
        public string? ClaimRole           { get; init; }
        public Guid?   ClaimToken          { get; init; }
        public DateTimeOffset? ClaimedAt   { get; init; }
        public DateTimeOffset? ClaimedUntil{ get; init; }
        public DateTimeOffset  CreatedAt   { get; init; }
        public DateTimeOffset  UpdatedAt   { get; init; }

        public WorkTask ToDomain() => new(
            Id, PlanId, PhaseId, ExternalKey, Title, Description, Paths, ReferencePointer,
            Priority, Status, AssigneeActorId, GithubBoardItemId, GithubIssueNumber, GithubIssueNodeId,
            new ClaimState(ClaimActorId, ClaimRole, ClaimToken, ClaimedAt, ClaimedUntil),
            CreatedAt, UpdatedAt);
    }
}

internal static class FindingRepoHelpers
{
    public static async Task<Finding?> GetByClaimAsync(IFindingRepository repo, Guid claimToken, CancellationToken ct)
    {
        var factoryField = repo.GetType().GetField("_factory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (factoryField?.GetValue(repo) is not IDbConnectionFactory factory)
            return null;
        await using var conn = await factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = """
            SELECT id AS Id, task_id AS TaskId, external_key AS ExternalKey,
                   severity AS Severity, invariant_impact AS InvariantImpact, symptom AS Symptom,
                   root_cause AS RootCause, repro_steps AS ReproSteps,
                   adversarial_input AS AdversarialInput, expected AS Expected, actual AS Actual,
                   reference_comparison AS ReferenceComparison, status AS Status,
                   claim_actor_id AS ClaimActorId, claim_role AS ClaimRole, claim_token AS ClaimToken,
                   claimed_at AS ClaimedAt, claimed_until AS ClaimedUntil,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM findings WHERE claim_token = @claimToken
            """;
        var row = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<FindingRowSnapshot>(
            conn, new Dapper.CommandDefinition(sql, new { claimToken }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDomain();
    }

    internal sealed record FindingRowSnapshot
    {
        public Guid    Id                  { get; init; }
        public Guid    TaskId              { get; init; }
        public string  ExternalKey         { get; init; } = "";
        public string? Severity            { get; init; }
        public string? InvariantImpact     { get; init; }
        public string? Symptom             { get; init; }
        public string? RootCause           { get; init; }
        public string? ReproSteps          { get; init; }
        public string? AdversarialInput    { get; init; }
        public string? Expected            { get; init; }
        public string? Actual              { get; init; }
        public string? ReferenceComparison { get; init; }
        public string  Status              { get; init; } = "";
        public Guid?   ClaimActorId        { get; init; }
        public string? ClaimRole           { get; init; }
        public Guid?   ClaimToken          { get; init; }
        public DateTimeOffset? ClaimedAt   { get; init; }
        public DateTimeOffset? ClaimedUntil{ get; init; }
        public DateTimeOffset  CreatedAt   { get; init; }
        public DateTimeOffset  UpdatedAt   { get; init; }

        public Finding ToDomain() => new(
            Id, TaskId, ExternalKey, Severity, InvariantImpact, Symptom, RootCause,
            ReproSteps, AdversarialInput, Expected, Actual, ReferenceComparison,
            Status,
            new ClaimState(ClaimActorId, ClaimRole, ClaimToken, ClaimedAt, ClaimedUntil),
            CreatedAt, UpdatedAt);
    }
}

internal static class NotificationHelpers
{
    public static async Task EnqueueBoardAndSlackAsync(
        Notifications.IBoardSyncEnqueue boardSync,
        Notifications.ISlackNotifyEnqueue slack,
        IPlanRepository plans,
        Plan plan,
        (PlanType Row, StateGraph Graph) pt,
        WorkTask task,
        string notificationType,
        RequestContext ctx,
        CancellationToken ct,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? extraTokens = null,
        string? assigneeGithubUsername = null,
        bool notifySlack = false)
    {
        if (plan.PrimaryBoardId is { } boardId)
        {
            var column = StateMachineService.ResolveBoardColumn(pt.Graph, task.Status, planOverride: null);
            await boardSync.EnqueueAsync(task.Id, boardId, column, task.Status, assigneeGithubUsername, ct).ConfigureAwait(false);
        }
        if (notifySlack)
            await slack.EnqueueForTaskAsync(plan, pt, task, notificationType, ctx, extraTokens, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Enqueue a board-sync row for a task with no Slack post. Called after a finding,
    /// attempt, or verdict mutation so the BoardSyncWorker re-renders the parent task
    /// card's audit ledger and Audit Stage field. No-op when the plan has no bound board.
    /// </summary>
    public static async Task EnqueueBoardRefreshAsync(
        Notifications.IBoardSyncEnqueue boardSync,
        Plan plan,
        (PlanType Row, StateGraph Graph) pt,
        WorkTask task,
        CancellationToken ct)
    {
        if (plan.PrimaryBoardId is { } boardId)
        {
            var column = StateMachineService.ResolveBoardColumn(pt.Graph, task.Status, planOverride: null);
            await boardSync.EnqueueAsync(task.Id, boardId, column, task.Status, ct: ct).ConfigureAwait(false);
        }
    }

    public static async Task EnqueueFindingSlackAsync(
        Notifications.ISlackNotifyEnqueue slack,
        IPlanRepository plans,
        Plan plan,
        (PlanType Row, StateGraph Graph) pt,
        Finding finding,
        Guid taskId,
        string notificationType,
        RequestContext ctx,
        CancellationToken ct,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? extraTokens = null)
    {
        await slack.EnqueueForFindingAsync(plan, pt, finding, taskId, notificationType, ctx, extraTokens, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Turn a verdict reason_category code ("not-reproducible") into channel-friendly
    /// prose ("not reproducible"). Returns null when no category was supplied.
    /// </summary>
    public static string? HumanizeReason(string? reasonCategory)
    {
        if (string.IsNullOrWhiteSpace(reasonCategory)) return null;
        return reasonCategory.Trim().Replace('-', ' ').Replace('_', ' ');
    }

    /// <summary>
    /// Cap a free-text reason to one short line for a Slack post. The full text still
    /// lives in the event log and the board card's audit ledger — Slack stays scannable.
    /// </summary>
    public static string TruncateReason(string? reason, int max = 180)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "";
        var s = System.Text.RegularExpressions.Regex.Replace(reason.Trim(), @"\s+", " ");
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Abbreviate a commit hash for display.</summary>
    public static string ShortHash(string? hash)
        => string.IsNullOrWhiteSpace(hash) ? "" : (hash!.Length <= 10 ? hash : hash[..10]);
}
