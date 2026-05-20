using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Data.Repositories;
using Workstream.Mcp.Notifications;
using Workstream.Core.StateMachine;

namespace Workstream.Mcp.Tools.Setup;

internal static class AdminGate
{
    public static void Require(RequestContext ctx)
    {
        if (!ctx.IsAdmin)
            throw new WorkstreamException(WorkstreamError.PermissionDenied("is_admin"));
    }
}

// ============================================================================
// create_project
// ============================================================================

public sealed record CreateProjectInput(string Slug, string DisplayName, string? Description = null);
public sealed record CreateProjectOutput(Guid Id, string Slug, string DisplayName);

public sealed class CreateProjectTool : McpTool<CreateProjectInput, CreateProjectOutput>
{
    private readonly IProjectRepository _repo;
    public CreateProjectTool(IProjectRepository repo) => _repo = repo;
    public override string Name => "create_project";
    public override string Description => "Admin: create a new project. Slug is the stable identifier used in URLs and CLIs.";

    protected override async Task<CreateProjectOutput> RunAsync(CreateProjectInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);
        var p = await _repo.CreateAsync(input.Slug, input.DisplayName, input.Description, ct).ConfigureAwait(false);
        return new CreateProjectOutput(p.Id, p.Slug, p.DisplayName);
    }
}

// ============================================================================
// add_project_repo
// ============================================================================

public sealed record AddProjectRepoInput(Guid ProjectId, string GithubOwner, string GithubRepo, bool IsReferenceOnly = false);
public sealed record AddProjectRepoOutput(Guid Id);

public sealed class AddProjectRepoTool : McpTool<AddProjectRepoInput, AddProjectRepoOutput>
{
    private readonly IProjectRepository _repo;
    public AddProjectRepoTool(IProjectRepository repo) => _repo = repo;
    public override string Name => "add_project_repo";
    public override string Description => "Admin: attach a GitHub repo to a project. Reference-only repos are not modified by the system (e.g. Chroma to MemTurbo).";

    protected override async Task<AddProjectRepoOutput> RunAsync(AddProjectRepoInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);
        var id = await _repo.AddRepoAsync(input.ProjectId, input.GithubOwner, input.GithubRepo, input.IsReferenceOnly, ct).ConfigureAwait(false);
        return new AddProjectRepoOutput(id);
    }
}

// ============================================================================
// add_project_board — minimal v1: caller supplies the discovered option ids
// ============================================================================

public sealed record AddProjectBoardInput(
    Guid     ProjectId,
    string   GithubProjectV2NodeId,
    int      GithubProjectNumber,
    string   GithubOwner,
    string   DisplayName,
    string   StatusFieldNodeId,
    string   StatusOptionBacklog,
    string   StatusOptionInProgress,
    string   StatusOptionReview,
    string   StatusOptionDone,
    string?  StatusOptionBlocked = null);
public sealed record AddProjectBoardOutput(Guid Id);

public sealed class AddProjectBoardTool : McpTool<AddProjectBoardInput, AddProjectBoardOutput>
{
    private readonly IProjectRepository _repo;
    public AddProjectBoardTool(IProjectRepository repo) => _repo = repo;
    public override string Name => "add_project_board";
    public override string Description =>
        "Admin: register a GitHub Projects V2 board for a project. Caller must have already discovered " +
        "the Status field option ids via the GraphQL API (the discover endpoint helps).";

    protected override async Task<AddProjectBoardOutput> RunAsync(AddProjectBoardInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);
        var board = new ProjectBoard
        {
            ProjectId              = input.ProjectId,
            GithubProjectV2NodeId  = input.GithubProjectV2NodeId,
            GithubProjectNumber    = input.GithubProjectNumber,
            GithubOwner            = input.GithubOwner,
            DisplayName            = input.DisplayName,
            StatusFieldNodeId      = input.StatusFieldNodeId,
            StatusOptionBacklog    = input.StatusOptionBacklog,
            StatusOptionInProgress = input.StatusOptionInProgress,
            StatusOptionReview     = input.StatusOptionReview,
            StatusOptionDone       = input.StatusOptionDone,
            StatusOptionBlocked    = input.StatusOptionBlocked,
            CreatedAt              = DateTimeOffset.UtcNow,
        };
        var id = await _repo.AddBoardAsync(board, ct).ConfigureAwait(false);
        return new AddProjectBoardOutput(id);
    }
}

// ============================================================================
// set_project_slack
// ============================================================================

public sealed record SetProjectSlackInput(Guid ProjectId, string WorkspaceId, string DefaultChannelId, string BotTokenSecretRef);
public sealed record SetProjectSlackOutput(Guid ProjectId);

public sealed class SetProjectSlackTool : McpTool<SetProjectSlackInput, SetProjectSlackOutput>
{
    private readonly IProjectRepository _repo;
    public SetProjectSlackTool(IProjectRepository repo) => _repo = repo;
    public override string Name => "set_project_slack";
    public override string Description => "Admin: configure the Slack workspace, default channel, and bot-token secret reference for a project.";

    protected override async Task<SetProjectSlackOutput> RunAsync(SetProjectSlackInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);
        await _repo.SetSlackAsync(input.ProjectId, input.WorkspaceId, input.BotTokenSecretRef, input.DefaultChannelId, ct).ConfigureAwait(false);
        return new SetProjectSlackOutput(input.ProjectId);
    }
}

// ============================================================================
// create_plan
// ============================================================================

public sealed record CreatePlanInput(
    Guid    ProjectId,
    string  PlanType,
    string  Name,
    string? Objective = null,
    Guid?   PrimaryBoardId = null,
    string? PrimarySlackChannelId = null);
public sealed record CreatePlanOutput(Guid Id, string Status);

public sealed class CreatePlanTool : McpTool<CreatePlanInput, CreatePlanOutput>
{
    private readonly IPlanRepository _repo;
    public CreatePlanTool(IPlanRepository repo) => _repo = repo;
    public override string Name => "create_plan";
    public override string Description =>
        "Admin: create a new plan (in draft status). Use activate_plan to move it to active and create the board items.";

    protected override async Task<CreatePlanOutput> RunAsync(CreatePlanInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);
        var p = await _repo.CreateAsync(input.ProjectId, input.PlanType, input.Name, input.Objective,
            ctx.ActorId, input.PrimaryBoardId, input.PrimarySlackChannelId, ct).ConfigureAwait(false);
        return new CreatePlanOutput(p.Id, p.Status);
    }
}

// ============================================================================
// add_phase
// ============================================================================

public sealed record AddPhaseInput(Guid PlanId, int OrderIndex, string Name);
public sealed record AddPhaseOutput(Guid Id);

public sealed class AddPhaseTool : McpTool<AddPhaseInput, AddPhaseOutput>
{
    private readonly IPlanRepository _repo;
    public AddPhaseTool(IPlanRepository repo) => _repo = repo;
    public override string Name => "add_phase";
    public override string Description => "Admin: add a phase to a plan. Phases are an optional structural layer; tasks may be unphased.";

    protected override async Task<AddPhaseOutput> RunAsync(AddPhaseInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);
        var id = await _repo.AddPhaseAsync(input.PlanId, input.OrderIndex, input.Name, ct).ConfigureAwait(false);
        return new AddPhaseOutput(id);
    }
}

// ============================================================================
// activate_plan
// ============================================================================

public sealed record ActivatePlanInput(Guid PlanId);
public sealed record ActivatePlanOutput(Guid Id, string Status, int BoardItemsEnqueued);

public sealed class ActivatePlanTool : McpTool<ActivatePlanInput, ActivatePlanOutput>
{
    private readonly IPlanRepository _plans;
    private readonly ITaskRepository _tasks;
    private readonly IPlanTypeCache _planTypes;
    private readonly IBoardSyncEnqueue _board;
    private readonly ISlackNotifyEnqueue _slack;

    public ActivatePlanTool(IPlanRepository plans, ITaskRepository tasks, IPlanTypeCache planTypes,
        IBoardSyncEnqueue board, ISlackNotifyEnqueue slack)
    {
        _plans = plans; _tasks = tasks; _planTypes = planTypes;
        _board = board; _slack = slack;
    }

    public override string Name => "activate_plan";
    public override string Description =>
        "Admin: move a plan from draft to active. Enqueues board-sync items for every task currently in the plan " +
        "and posts a plan.activated Slack notification.";

    protected override async Task<ActivatePlanOutput> RunAsync(ActivatePlanInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);

        // An archived plan is retired for good — activate_plan must not resurrect it.
        var existing = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                       ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        PlanGuards.EnsureNotArchived(existing);

        var plan = await _plans.SetStatusAsync(input.PlanId, PlanStatus.Active, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));

        var pt = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                 ?? throw new WorkstreamException(new WorkstreamError(ErrorCodes.PlanTypeUnknown, plan.PlanTypeId));

        var enqueued = 0;
        if (plan.PrimaryBoardId is { } boardId)
        {
            var tasks = await _tasks.ListByPlanAsync(plan.Id, ct).ConfigureAwait(false);
            foreach (var t in tasks)
            {
                var column = StateMachineService.ResolveBoardColumn(pt.Graph, t.Status);
                await _board.EnqueueAsync(t.Id, boardId, column, t.Status, ct: ct).ConfigureAwait(false);
                enqueued++;
            }
        }

        await _slack.EnqueueForPlanAsync(plan, pt, "plan.activated", ctx, ct).ConfigureAwait(false);
        return new ActivatePlanOutput(plan.Id, plan.Status, enqueued);
    }
}

// ============================================================================
// archive_plan
// ============================================================================

public sealed record ArchivePlanInput(Guid PlanId, string? Reason = null);
public sealed record ArchivePlanOutput(Guid Id, string Status, bool AlreadyArchived);

public sealed class ArchivePlanTool : McpTool<ArchivePlanInput, ArchivePlanOutput>
{
    private readonly IPlanRepository _plans;
    private readonly IPlanTypeCache _planTypes;
    private readonly IEventRepository _events;
    private readonly ISlackNotifyEnqueue _slack;

    public ArchivePlanTool(IPlanRepository plans, IPlanTypeCache planTypes,
        IEventRepository events, ISlackNotifyEnqueue slack)
    {
        _plans = plans; _planTypes = planTypes; _events = events; _slack = slack;
    }

    public override string Name => "archive_plan";
    public override string Description =>
        "Archive (disable) a plan so it stops accepting work. Requires can_archive_plan on the " +
        "calling actor. An archived plan is frozen: claim_next_task, claim_specific_task, the " +
        "finding-claim tools, create_task / create_tasks, and activate_plan all reject it with the " +
        "structured plan_archived error. Use this to retire a finished or abandoned plan — the next " +
        "time an orchestrator bootstraps it will find no active plan of that type and create a fresh " +
        "one, which is how you start a new audit/development pass on demand. Archiving is one-way " +
        "(there is no un-archive) and idempotent; the plan's tasks, events, and board cards are left " +
        "intact for forensics. Posts a plan.archived Slack notice. Returns the plan id, its new " +
        "status, and already_archived (true when the plan was already archived and nothing changed).";

    protected override async Task<ArchivePlanOutput> RunAsync(ArchivePlanInput input, RequestContext ctx, CancellationToken ct)
    {
        if (!ctx.HasPermission("can_archive_plan"))
            throw new WorkstreamException(WorkstreamError.PermissionDenied("can_archive_plan"));

        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));

        // Idempotent: archiving an already-archived plan changes nothing — no event, no Slack.
        if (plan.Status == PlanStatus.Archived)
            return new ArchivePlanOutput(plan.Id, plan.Status, AlreadyArchived: true);

        var previousStatus = plan.Status;
        var archived = await _plans.SetStatusAsync(input.PlanId, PlanStatus.Archived, ct).ConfigureAwait(false)
                       ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));

        // The events table is append-only and load-bearing for forensics: record who
        // archived the plan, the status it came from, and the operator's reason.
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason!.Trim();
        await _events.EmitAsync(
            ctx.ActorId, EntityType.Plan, archived.Id, "archived",
            fromState: previousStatus, toState: PlanStatus.Archived,
            payloadJson: JsonSerializer.Serialize(new { reason }), ct).ConfigureAwait(false);

        // Best-effort Slack — a missing plan-type must not fail the archive itself.
        var pt = await _planTypes.GetAsync(archived.PlanTypeId, ct).ConfigureAwait(false);
        if (pt is not null)
            await _slack.EnqueueForPlanAsync(archived, pt.Value, "plan.archived", ctx, ct).ConfigureAwait(false);

        return new ArchivePlanOutput(archived.Id, archived.Status, AlreadyArchived: false);
    }
}
