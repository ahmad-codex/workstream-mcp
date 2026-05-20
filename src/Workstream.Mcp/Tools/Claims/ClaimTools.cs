using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Core.StateMachine;
using Workstream.Data.Repositories;

namespace Workstream.Mcp.Tools.Claims;

internal static class ClaimHelpers
{
    public static TimeSpan ResolveTtl(string roleTtlsJson, string role, TimeSpan? requested)
    {
        if (requested is { } r) return r;
        // role_ttls is a flat object of role → human-readable duration ("30m", "4h").
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(roleTtlsJson);
            if (doc.RootElement.TryGetProperty(role, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                return ParseDuration(el.GetString() ?? "");
        }
        catch
        {
            // fall through
        }
        return TimeSpan.FromMinutes(30);
    }

    public static TimeSpan ParseDuration(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.FromMinutes(30);
        var span = text.AsSpan();
        var suffix = span[^1];
        var numericPart = span[..^1];
        if (!int.TryParse(numericPart, out var n)) return TimeSpan.FromMinutes(30);
        return suffix switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _   => TimeSpan.FromMinutes(30),
        };
    }
}

// ============================================================================
// claim_next_task
// ============================================================================

public sealed record ClaimNextTaskInput(Guid PlanId, string Role, string? TtlOverride = null);
public sealed record ClaimNextTaskOutput(Guid ClaimToken, Guid TaskId, string Title, string Status, DateTimeOffset ClaimedUntil);

public sealed class ClaimNextTaskTool : McpTool<ClaimNextTaskInput, ClaimNextTaskOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly Workstream.Mcp.Notifications.IBoardSyncEnqueue _board;

    public ClaimNextTaskTool(ITaskRepository tasks, IPlanRepository plans, IPlanTypeCache planTypes,
        Workstream.Mcp.Notifications.IBoardSyncEnqueue board)
    {
        _tasks = tasks;
        _plans = plans;
        _planTypes = planTypes;
        _board = board;
    }

    public override string Name => "claim_next_task";
    public override string Description =>
        "Atomically claim the highest-priority pending task on a plan for the given role. The role " +
        "must be valid for the plan's plan_type (e.g. 'developer' for dev plans, 'auditor' for audit). " +
        "Returns { task, claim_token } on success or no_work_available if nothing is claimable. Use " +
        "this when the orchestrator or developer is ready to start something new and the system should " +
        "pick. Assigns the bound Project V2 card to the calling actor's GitHub user.";

    protected override async Task<ClaimNextTaskOutput> RunAsync(ClaimNextTaskInput input, RequestContext ctx, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        PlanGuards.EnsureNotArchived(plan);
        var (pt, graph) = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, $"plan_type '{plan.PlanTypeId}'"));
        var requested = input.TtlOverride is null ? (TimeSpan?)null : ClaimHelpers.ParseDuration(input.TtlOverride);
        var ttl = ClaimHelpers.ResolveTtl(pt.RoleTtlsJson, input.Role, requested);

        var claimed = await _tasks.ClaimNextAsync(input.PlanId, ctx.ActorId, input.Role, ttl, ct).ConfigureAwait(false);
        if (claimed is null)
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.NoWorkAvailable, "no claimable task on this plan"));

        if (plan.PrimaryBoardId is { } boardId)
        {
            var column = Workstream.Core.StateMachine.StateMachineService.ResolveBoardColumn(graph, claimed.Task.Status);
            await _board.EnqueueAsync(claimed.Task.Id, boardId, column, claimed.Task.Status, ctx.GithubUsername, ct).ConfigureAwait(false);
        }

        return new ClaimNextTaskOutput(
            claimed.ClaimToken,
            claimed.Task.Id,
            claimed.Task.Title,
            claimed.Task.Status,
            claimed.Task.Claim.ClaimedUntil ?? DateTimeOffset.UtcNow.Add(ttl));
    }
}

// ============================================================================
// claim_specific_task
// ============================================================================

public sealed record ClaimSpecificTaskInput(Guid TaskId, string Role, string? TtlOverride = null);

public sealed class ClaimSpecificTaskTool : McpTool<ClaimSpecificTaskInput, ClaimNextTaskOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly Workstream.Mcp.Notifications.IBoardSyncEnqueue _board;

    public ClaimSpecificTaskTool(ITaskRepository tasks, IPlanRepository plans, IPlanTypeCache planTypes,
        Workstream.Mcp.Notifications.IBoardSyncEnqueue board)
    {
        _tasks = tasks;
        _plans = plans;
        _planTypes = planTypes;
        _board = board;
    }

    public override string Name => "claim_specific_task";
    public override string Description =>
        "Claim a specific task by id. Fails with task_unavailable if another actor holds it with an " +
        "unexpired claim. Use when the orchestrator is directing work (e.g. dispatching a subagent " +
        "to a particular task). Assigns the bound Project V2 card to the calling actor's GitHub user.";

    protected override async Task<ClaimNextTaskOutput> RunAsync(ClaimSpecificTaskInput input, RequestContext ctx, CancellationToken ct)
    {
        var task = await _tasks.GetAsync(input.TaskId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        PlanGuards.EnsureNotArchived(plan);
        var (pt, graph) = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));
        var requested = input.TtlOverride is null ? (TimeSpan?)null : ClaimHelpers.ParseDuration(input.TtlOverride);
        var ttl = ClaimHelpers.ResolveTtl(pt.RoleTtlsJson, input.Role, requested);

        var claimed = await _tasks.ClaimSpecificAsync(input.TaskId, ctx.ActorId, input.Role, ttl, ct).ConfigureAwait(false);
        if (claimed is null)
        {
            // Distinguish "blocked by unmet dependency" from "held by another actor" so the
            // orchestrator can react. dependencies_unmet carries the blocker ids in details.
            var unmet = await _tasks.GetUnmetDependenciesAsync(input.TaskId, ct).ConfigureAwait(false);
            if (unmet.Count > 0)
            {
                throw new WorkstreamException(new WorkstreamError(
                    ErrorCodes.DependenciesUnmet,
                    $"task has {unmet.Count} unmet dependencies (predecessors not in done/deferred/skipped/out_of_scope)",
                    new Dictionary<string, object?> { ["unmet_dependencies"] = unmet }));
            }
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.TaskUnavailable, "task is currently held by another actor"));
        }

        if (plan.PrimaryBoardId is { } boardId)
        {
            var column = Workstream.Core.StateMachine.StateMachineService.ResolveBoardColumn(graph, claimed.Task.Status);
            await _board.EnqueueAsync(claimed.Task.Id, boardId, column, claimed.Task.Status, ctx.GithubUsername, ct).ConfigureAwait(false);
        }

        return new ClaimNextTaskOutput(
            claimed.ClaimToken, claimed.Task.Id, claimed.Task.Title, claimed.Task.Status,
            claimed.Task.Claim.ClaimedUntil ?? DateTimeOffset.UtcNow.Add(ttl));
    }
}

// ============================================================================
// claim_next_finding_for_verification
// ============================================================================

public sealed record ClaimFindingInput(Guid PlanId);
public sealed record ClaimedFindingOutput(Guid ClaimToken, Guid FindingId, Guid TaskId, string Status, string? Severity);

public sealed class ClaimNextFindingForVerificationTool : McpTool<ClaimFindingInput, ClaimedFindingOutput>
{
    private readonly IFindingRepository _findings;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;

    public ClaimNextFindingForVerificationTool(IFindingRepository findings, IPlanRepository plans, IPlanTypeCache planTypes)
    {
        _findings = findings; _plans = plans; _planTypes = planTypes;
    }

    public override string Name => "claim_next_finding_for_verification";
    public override string Description =>
        "Audit-plan-only: claim the highest-severity pending-verification finding on the plan. The " +
        "calling actor is implicitly assigned the 'verifier' role for the claim duration. Returns " +
        "no_work_available if no findings are waiting.";

    protected override async Task<ClaimedFindingOutput> RunAsync(ClaimFindingInput input, RequestContext ctx, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        PlanGuards.EnsureNotArchived(plan);
        var (pt, _) = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));
        var ttl = ClaimHelpers.ResolveTtl(pt.RoleTtlsJson, "verifier", null);
        var c = await _findings.ClaimNextForVerificationAsync(input.PlanId, ctx.ActorId, ttl, ct).ConfigureAwait(false);
        if (c is null)
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.NoWorkAvailable, "no findings awaiting verification"));
        return new ClaimedFindingOutput(c.ClaimToken, c.Finding.Id, c.Finding.TaskId, c.Finding.Status, c.Finding.Severity);
    }
}

// ============================================================================
// claim_next_finding_for_fix
// ============================================================================

public sealed class ClaimNextFindingForFixTool : McpTool<ClaimFindingInput, ClaimedFindingOutput>
{
    private readonly IFindingRepository _findings;
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;

    public ClaimNextFindingForFixTool(IFindingRepository findings, IPlanRepository plans, IPlanTypeCache planTypes)
    {
        _findings = findings; _plans = plans; _planTypes = planTypes;
    }

    public override string Name => "claim_next_finding_for_fix";
    public override string Description =>
        "Audit-plan-only: claim the highest-severity confirmed (or below-cap fix_failed/partial) " +
        "finding to fix. Caller is implicitly the 'fixer' for the claim. Sets the finding status " +
        "to in_fix as part of the claim transaction.";

    protected override async Task<ClaimedFindingOutput> RunAsync(ClaimFindingInput input, RequestContext ctx, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        PlanGuards.EnsureNotArchived(plan);
        var (pt, _) = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));
        var ttl = ClaimHelpers.ResolveTtl(pt.RoleTtlsJson, "fixer", null);
        var c = await _findings.ClaimNextForFixAsync(input.PlanId, ctx.ActorId, ttl, pt.RetryCap, ct).ConfigureAwait(false);
        if (c is null)
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.NoWorkAvailable, "no findings awaiting fix"));
        return new ClaimedFindingOutput(c.ClaimToken, c.Finding.Id, c.Finding.TaskId, c.Finding.Status, c.Finding.Severity);
    }
}

// ============================================================================
// claim_next_attempt_for_review — stubbed: there's no separate attempt claim queue in v1,
// the verdict tools take a finding/attempt id directly. Kept on the surface for future expansion.
// ============================================================================

public sealed record NoOpOutput(string Message);

public sealed class ClaimNextAttemptForReviewTool : McpTool<ClaimFindingInput, NoOpOutput>
{
    public override string Name => "claim_next_attempt_for_review";
    public override string Description =>
        "Reserved tool surface for the future fix-verifier / dev-reviewer queue. In v1 the verdict " +
        "tools (submit_attempt_verdict / submit_review_decision) accept the attempt id directly from " +
        "the orchestrator's working state, so no separate claim is needed.";

    protected override Task<NoOpOutput> RunAsync(ClaimFindingInput _, RequestContext __, CancellationToken ___)
        => throw new WorkstreamException(new WorkstreamError(
            ErrorCodes.NoWorkAvailable,
            "explicit attempt-claim queue not implemented in v1; use submit_attempt_verdict with the attempt id"));
}

// ============================================================================
// refresh_claim
// ============================================================================

public sealed record RefreshClaimInput(Guid ClaimToken, string? ExtendBy = null);
public sealed record RefreshClaimOutput(string EntityType, Guid EntityId, DateTimeOffset ClaimedUntil);

public sealed class RefreshClaimTool : McpTool<RefreshClaimInput, RefreshClaimOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IFindingRepository _findings;
    private readonly IPlanRepository _plans;
    private readonly Workstream.Core.StateMachine.IPlanTypeCache _planTypes;

    public RefreshClaimTool(
        ITaskRepository tasks,
        IFindingRepository findings,
        IPlanRepository plans,
        Workstream.Core.StateMachine.IPlanTypeCache planTypes)
    {
        _tasks = tasks; _findings = findings; _plans = plans; _planTypes = planTypes;
    }

    public override string Name => "refresh_claim";
    public override string Description =>
        "Extend an active claim's TTL without releasing it. Use when a subagent's reproduction is " +
        "taking longer than the configured role TTL (e.g. a Hetzner-routed verifier mid-deploy). " +
        "extend_by accepts the same duration shorthand as the role_ttls config ('30m', '2h'); if " +
        "omitted, the role's configured TTL is added on top of the current claimed_until. The token " +
        "must still be live AND held by the calling actor — an already-expired claim cannot be " +
        "reanimated here (it must release and re-claim), because the stuck-work sweeper may have " +
        "already handed the row to someone else. Writes a claim_refreshed event so the audit trail " +
        "shows every extension.";

    protected override async Task<RefreshClaimOutput> RunAsync(RefreshClaimInput input, RequestContext ctx, CancellationToken ct)
    {
        // Resolve the requested extension before touching the DB so the same parsing path
        // serves both task and finding routes below.
        TimeSpan? requested = input.ExtendBy is null ? null : ClaimHelpers.ParseDuration(input.ExtendBy);

        // Try the task path first. It's the common case and the lookup is cheap.
        var task = await _tasks.GetByClaimTokenAsync(input.ClaimToken, ct).ConfigureAwait(false);
        if (task is not null)
        {
            var ttl = requested ?? await ResolveTtlForTaskAsync(task, ct).ConfigureAwait(false);
            var updated = await _tasks.RefreshClaimAsync(input.ClaimToken, ctx.ActorId, ttl, ct).ConfigureAwait(false)
                          ?? throw new WorkstreamException(WorkstreamError.StaleClaim());
            return new RefreshClaimOutput(EntityType.Task, updated.Id,
                updated.Claim.ClaimedUntil ?? DateTimeOffset.UtcNow.Add(ttl));
        }

        // Fall through to the finding path. Same liveness gate, same actor check.
        var finding = await _findings.GetByClaimTokenAsync(input.ClaimToken, ct).ConfigureAwait(false);
        if (finding is not null)
        {
            var ttl = requested ?? await ResolveTtlForFindingAsync(finding, ct).ConfigureAwait(false);
            var updated = await _findings.RefreshClaimAsync(input.ClaimToken, ctx.ActorId, ttl, ct).ConfigureAwait(false)
                          ?? throw new WorkstreamException(WorkstreamError.StaleClaim());
            return new RefreshClaimOutput(EntityType.Finding, updated.Id,
                updated.Claim.ClaimedUntil ?? DateTimeOffset.UtcNow.Add(ttl));
        }

        throw new WorkstreamException(WorkstreamError.StaleClaim());
    }

    private async Task<TimeSpan> ResolveTtlForTaskAsync(WorkTask task, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false);
        if (plan is null) return TimeSpan.FromMinutes(30);
        var ptOpt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
        if (ptOpt is null) return TimeSpan.FromMinutes(30);
        var role = task.Claim.Role ?? "";
        return ClaimHelpers.ResolveTtl(ptOpt.Value.Row.RoleTtlsJson, role, requested: null);
    }

    private async Task<TimeSpan> ResolveTtlForFindingAsync(Finding finding, CancellationToken ct)
    {
        // Findings don't carry plan_id directly; resolve through the parent task.
        var task = await _tasks.GetAsync(finding.TaskId, ct).ConfigureAwait(false);
        if (task is null) return TimeSpan.FromMinutes(30);
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false);
        if (plan is null) return TimeSpan.FromMinutes(30);
        var ptOpt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
        if (ptOpt is null) return TimeSpan.FromMinutes(30);
        var role = finding.Claim.Role ?? "";
        return ClaimHelpers.ResolveTtl(ptOpt.Value.Row.RoleTtlsJson, role, requested: null);
    }
}

// ============================================================================
// release_claim
// ============================================================================

public sealed record ReleaseClaimInput(Guid ClaimToken, string? Reason = null);
public sealed record ReleaseClaimOutput(string EntityType, Guid EntityId, string NewStatus);

public sealed class ReleaseClaimTool : McpTool<ReleaseClaimInput, ReleaseClaimOutput>
{
    private readonly ITaskRepository _tasks;
    private readonly IFindingRepository _findings;
    private readonly IPlanRepository _plans;
    private readonly Workstream.Core.StateMachine.IPlanTypeCache _planTypes;
    private readonly Workstream.Mcp.Notifications.IBoardSyncEnqueue _board;
    private readonly Workstream.Mcp.Notifications.ISlackNotifyEnqueue _slack;

    public ReleaseClaimTool(
        ITaskRepository tasks, IFindingRepository findings,
        IPlanRepository plans, Workstream.Core.StateMachine.IPlanTypeCache planTypes,
        Workstream.Mcp.Notifications.IBoardSyncEnqueue board,
        Workstream.Mcp.Notifications.ISlackNotifyEnqueue slack)
    {
        _tasks = tasks; _findings = findings;
        _plans = plans; _planTypes = planTypes; _board = board; _slack = slack;
    }

    public override string Name => "release_claim";
    public override string Description =>
        "Release a claim without submitting work. Used when an agent decides it cannot complete the " +
        "work and wants to surface it back to the pool. The task status reverts to pending, the " +
        "bound board's card moves back to the Backlog column (lazy-created on the project if it " +
        "wasn't there yet), and its assignees are cleared so the next claimer takes ownership cleanly. " +
        "Slack is intentionally not posted because routine claim cycles are noise.";

    protected override async Task<ReleaseClaimOutput> RunAsync(ReleaseClaimInput input, RequestContext ctx, CancellationToken ct)
    {
        var task = await _tasks.ReleaseClaimAsync(input.ClaimToken, ctx.ActorId, input.Reason, resetStatusToPending: true, ct).ConfigureAwait(false);
        if (task is not null)
        {
            // Move the card back to Backlog. Don't fire Slack — release_claim is normal
            // claim churn and shouldn't generate channel noise.
            var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false);
            if (plan?.PrimaryBoardId is { } boardId)
            {
                var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false);
                if (pt is not null)
                {
                    var column = Workstream.Core.StateMachine.StateMachineService.ResolveBoardColumn(pt.Value.Graph, task.Status);
                    // Empty string = clear assignees on the bound card so the Backlog
                    // doesn't show a phantom owner after the work returns to the pool.
                    await _board.EnqueueAsync(task.Id, boardId, column, task.Status, assigneeGithubUsername: "", ct: ct).ConfigureAwait(false);
                }
            }
            return new ReleaseClaimOutput(EntityType.Task, task.Id, task.Status);
        }

        // Try finding/attempt — not yet implemented in their repos for release-only semantics.
        throw new WorkstreamException(WorkstreamError.StaleClaim());
    }
}
