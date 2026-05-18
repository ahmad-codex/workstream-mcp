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

    public ClaimNextTaskTool(ITaskRepository tasks, IPlanRepository plans, IPlanTypeCache planTypes)
    {
        _tasks = tasks;
        _plans = plans;
        _planTypes = planTypes;
    }

    public override string Name => "claim_next_task";
    public override string Description =>
        "Atomically claim the highest-priority pending task on a plan for the given role. The role " +
        "must be valid for the plan's plan_type (e.g. 'developer' for dev plans, 'auditor' for audit). " +
        "Returns { task, claim_token } on success or no_work_available if nothing is claimable. Use " +
        "this when the orchestrator or developer is ready to start something new and the system should " +
        "pick.";

    protected override async Task<ClaimNextTaskOutput> RunAsync(ClaimNextTaskInput input, RequestContext ctx, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var (pt, _) = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, $"plan_type '{plan.PlanTypeId}'"));
        var requested = input.TtlOverride is null ? (TimeSpan?)null : ClaimHelpers.ParseDuration(input.TtlOverride);
        var ttl = ClaimHelpers.ResolveTtl(pt.RoleTtlsJson, input.Role, requested);

        var claimed = await _tasks.ClaimNextAsync(input.PlanId, ctx.ActorId, input.Role, ttl, ct).ConfigureAwait(false);
        if (claimed is null)
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.NoWorkAvailable, "no claimable task on this plan"));

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

    public ClaimSpecificTaskTool(ITaskRepository tasks, IPlanRepository plans, IPlanTypeCache planTypes)
    {
        _tasks = tasks;
        _plans = plans;
        _planTypes = planTypes;
    }

    public override string Name => "claim_specific_task";
    public override string Description =>
        "Claim a specific task by id. Fails with task_unavailable if another actor holds it with an " +
        "unexpired claim. Use when the orchestrator is directing work (e.g. dispatching a subagent " +
        "to a particular task).";

    protected override async Task<ClaimNextTaskOutput> RunAsync(ClaimSpecificTaskInput input, RequestContext ctx, CancellationToken ct)
    {
        var task = await _tasks.GetAsync(input.TaskId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("task"));
        var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var (pt, _) = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                      ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));
        var requested = input.TtlOverride is null ? (TimeSpan?)null : ClaimHelpers.ParseDuration(input.TtlOverride);
        var ttl = ClaimHelpers.ResolveTtl(pt.RoleTtlsJson, input.Role, requested);

        var claimed = await _tasks.ClaimSpecificAsync(input.TaskId, ctx.ActorId, input.Role, ttl, ct).ConfigureAwait(false);
        if (claimed is null)
            throw new WorkstreamException(new WorkstreamError(ErrorCodes.TaskUnavailable, "task is currently held by another actor"));

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
        "work and wants to surface it back to the pool. The task status reverts to pending and the " +
        "bound board's card moves back to the Backlog column (lazy-created on the project if it " +
        "wasn't there yet). Slack is intentionally not posted because routine claim cycles are noise.";

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
                    await _board.EnqueueAsync(task.Id, boardId, column, task.Status, ct).ConfigureAwait(false);
                }
            }
            return new ReleaseClaimOutput(EntityType.Task, task.Id, task.Status);
        }

        // Try finding/attempt — not yet implemented in their repos for release-only semantics.
        throw new WorkstreamException(WorkstreamError.StaleClaim());
    }
}
