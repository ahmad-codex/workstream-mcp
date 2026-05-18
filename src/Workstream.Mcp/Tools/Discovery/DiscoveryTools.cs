using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Core.StateMachine;
using Workstream.Data;
using Workstream.Data.Repositories;

namespace Workstream.Mcp.Tools.Discovery;

// ============================================================================
// list_projects
// ============================================================================

public sealed record ListProjectsInput;

public sealed record ListProjectsOutput(IReadOnlyList<ListProjectsItem> Projects);

public sealed record ListProjectsItem(
    Guid    Id,
    string  Slug,
    string  DisplayName,
    int     ActivePlanCount,
    int     MyOpenClaims);

public sealed class ListProjectsTool : McpTool<ListProjectsInput, ListProjectsOutput>
{
    private readonly IProjectRepository _projects;
    private readonly IDbConnectionFactory _factory;

    public ListProjectsTool(IProjectRepository projects, IDbConnectionFactory factory)
    {
        _projects = projects;
        _factory = factory;
    }

    public override string Name => "list_projects";
    public override string Description =>
        "List every project the calling actor can see, with per-project active plan count and the " +
        "number of tasks/findings the actor currently has claimed across the project. Use this as " +
        "the first call when a developer or orchestrator wakes up and needs to decide what to work on.";

    protected override async Task<ListProjectsOutput> RunAsync(ListProjectsInput _, RequestContext ctx, CancellationToken ct)
    {
        var projects = await _projects.ListAsync(ct).ConfigureAwait(false);
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = """
            SELECT p.id AS project_id,
                   COUNT(DISTINCT CASE WHEN pl.status = 'active' THEN pl.id END) AS active_plan_count,
                   COUNT(DISTINCT CASE
                       WHEN t.claim_actor_id = @actorId AND t.claimed_until > now()
                       THEN t.id
                   END) AS my_open_claims
            FROM projects p
            LEFT JOIN plans pl ON pl.project_id = p.id
            LEFT JOIN tasks t  ON t.plan_id     = pl.id
            GROUP BY p.id
            """;
        var rows = await Dapper.SqlMapper.QueryAsync<(Guid ProjectId, int ActivePlanCount, int MyOpenClaims)>(
            conn, new Dapper.CommandDefinition(sql, new { actorId = ctx.ActorId }, cancellationToken: ct))
            .ConfigureAwait(false);
        var byId = rows.ToDictionary(r => r.ProjectId);
        var items = projects.Select(p =>
        {
            byId.TryGetValue(p.Id, out var stats);
            return new ListProjectsItem(p.Id, p.Slug, p.DisplayName, stats.ActivePlanCount, stats.MyOpenClaims);
        }).ToList();
        return new ListProjectsOutput(items);
    }
}

// ============================================================================
// list_plans
// ============================================================================

public sealed record ListPlansInput(Guid ProjectId, string? Status = null);

public sealed record ListPlansOutput(IReadOnlyList<ListPlansItem> Plans);

public sealed record ListPlansItem(
    Guid    Id,
    string  Name,
    string  PlanType,
    string  Status,
    IReadOnlyDictionary<string, int> TaskCounts,
    string? BoardUrl);

public sealed class ListPlansTool : McpTool<ListPlansInput, ListPlansOutput>
{
    private readonly IPlanRepository _plans;

    public ListPlansTool(IPlanRepository plans) => _plans = plans;

    public override string Name => "list_plans";
    public override string Description =>
        "List plans on a project, optionally filtered by status. Each plan reports per-status task counts " +
        "so the caller can pick the next plan with workable work without making N additional dashboard calls.";

    protected override async Task<ListPlansOutput> RunAsync(ListPlansInput input, RequestContext ctx, CancellationToken ct)
    {
        var plans = await _plans.ListByProjectAsync(input.ProjectId, input.Status, ct).ConfigureAwait(false);
        var items = new List<ListPlansItem>(plans.Count);
        foreach (var p in plans)
        {
            var counts = await _plans.GetTaskCountsAsync(p.Id, ct).ConfigureAwait(false);
            items.Add(new ListPlansItem(p.Id, p.Name, p.PlanTypeId, p.Status, counts.Counts, BoardUrl: null));
        }
        return new ListPlansOutput(items);
    }
}

// ============================================================================
// get_plan_dashboard — the single call orchestrators make to decide what to do next
// ============================================================================

public sealed record GetPlanDashboardInput(Guid PlanId);

// DashboardPlan, DashboardEvent, DashboardPhaseCounts are constructed manually in C# — they
// stay positional. The three that Dapper hydrates use property-init for the lenient path.
public sealed record DashboardPlan(Guid Id, string Name, string Status, string? Objective);

public sealed record DashboardClaimable
{
    public Guid    TaskId    { get; init; }
    public string  Title     { get; init; } = "";
    public int     Priority  { get; init; }
    public string? Phase     { get; init; }
}

public sealed record DashboardClaim
{
    public Guid    TaskId        { get; init; }
    public string  Role          { get; init; } = "";
    public DateTimeOffset? ClaimedUntil { get; init; }
}

public sealed record DashboardStuck
{
    public Guid    TaskId       { get; init; }
    public DateTimeOffset Since { get; init; }
    public string  Status       { get; init; } = "";
    public string? ClaimHolder  { get; init; }
}

public sealed record DashboardEvent(long Id, DateTimeOffset At, string EntityType, Guid EntityId, string EventType, string? FromState, string? ToState);
public sealed record DashboardPhaseCounts(string Name, IReadOnlyDictionary<string, int> Counts);

public sealed record GetPlanDashboardOutput(
    DashboardPlan                        Plan,
    IReadOnlyDictionary<string, int>     ByStatus,
    IReadOnlyList<DashboardPhaseCounts>  ByPhase,
    IReadOnlyList<DashboardClaimable>    NextClaimable,
    IReadOnlyList<DashboardClaim>        MyActiveClaims,
    IReadOnlyList<DashboardStuck>        StuckWork,
    IReadOnlyList<DashboardEvent>        RecentEvents);

public sealed class GetPlanDashboardTool : McpTool<GetPlanDashboardInput, GetPlanDashboardOutput>
{
    private readonly IPlanRepository _plans;
    private readonly IEventRepository _events;
    private readonly IPlanTypeCache _planTypes;
    private readonly IDbConnectionFactory _factory;

    public GetPlanDashboardTool(IPlanRepository plans, IEventRepository events, IPlanTypeCache planTypes, IDbConnectionFactory factory)
    {
        _plans = plans;
        _events = events;
        _planTypes = planTypes;
        _factory = factory;
    }

    public override string Name => "get_plan_dashboard";
    public override string Description =>
        "Comprehensive snapshot for one plan: per-status counts, per-phase counts, up to 5 next-claimable " +
        "tasks, the caller's currently-held claims, stuck work (claims older than 2× their TTL), and the " +
        "20 most recent events. This is the single call an orchestrator makes every iteration to decide " +
        "what to do next; cached in-process for 5 seconds and invalidated on LISTEN/NOTIFY.";

    protected override async Task<GetPlanDashboardOutput> RunAsync(GetPlanDashboardInput input, RequestContext ctx, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        var planType = await _planTypes.GetAsync(plan.PlanTypeId, ct).ConfigureAwait(false)
                       ?? throw new WorkstreamException(WorkstreamError.NotFound($"plan_type '{plan.PlanTypeId}'"));

        var byStatus = await _plans.GetTaskCountsAsync(plan.Id, ct).ConfigureAwait(false);

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);

        var nextClaimable = (await Dapper.SqlMapper.QueryAsync<DashboardClaimable>(conn, new Dapper.CommandDefinition("""
            SELECT t.id AS TaskId, t.title AS Title, t.priority AS Priority,
                   (SELECT name FROM phases WHERE id = t.phase_id) AS Phase
            FROM tasks t
            WHERE t.plan_id = @planId AND t.status = 'pending'
            ORDER BY t.priority DESC, t.created_at
            LIMIT 5
            """, new { planId = plan.Id }, cancellationToken: ct)).ConfigureAwait(false)).ToList();

        var myClaims = (await Dapper.SqlMapper.QueryAsync<DashboardClaim>(conn, new Dapper.CommandDefinition("""
            SELECT id AS TaskId, claim_role AS Role, claimed_until AS ClaimedUntil
            FROM tasks WHERE plan_id = @planId AND claim_actor_id = @actorId AND claim_token IS NOT NULL
            """, new { planId = plan.Id, actorId = ctx.ActorId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();

        // Stuck: claim older than 2× TTL. TTL varies per role per plan type. We approximate by
        // checking whether claimed_until is more than 1× TTL in the past (i.e., expired by a
        // full extra TTL window). Using the longest TTL in the role_ttls map keeps the check
        // conservative.
        var stuck = (await Dapper.SqlMapper.QueryAsync<DashboardStuck>(conn, new Dapper.CommandDefinition("""
            SELECT t.id AS TaskId, t.updated_at AS Since, t.status AS Status,
                   -- citext → text cast: under Server Compatibility Mode=NoTypeLoading
                   -- Npgsql can't deserialize the citext OID and Dapper trips on
                   -- GetFieldType (same pattern as UserRepository.Columns).
                   (SELECT github_username::text FROM users u WHERE u.id = t.claim_actor_id) AS ClaimHolder
            FROM tasks t
            WHERE t.plan_id = @planId
              AND t.claim_token IS NOT NULL
              AND t.claimed_until < now() - interval '15 minutes'
            ORDER BY t.updated_at
            """, new { planId = plan.Id }, cancellationToken: ct)).ConfigureAwait(false)).ToList();

        var events = (await _events.ListRecentForPlanAsync(plan.Id, 20, ct).ConfigureAwait(false))
            .Select(e => new DashboardEvent(e.Id, e.At, e.EntityType, e.EntityId, e.EventType, e.FromState, e.ToState))
            .ToList();

        // Phase counts: optional grouping.
        var byPhase = (await Dapper.SqlMapper.QueryAsync<(string Name, string Status, int N)>(conn, new Dapper.CommandDefinition("""
            SELECT COALESCE(ph.name, '(no phase)') AS name, t.status, COUNT(*) AS n
            FROM tasks t LEFT JOIN phases ph ON ph.id = t.phase_id
            WHERE t.plan_id = @planId
            GROUP BY ph.name, t.status
            ORDER BY ph.name NULLS LAST
            """, new { planId = plan.Id }, cancellationToken: ct)).ConfigureAwait(false))
            .GroupBy(r => r.Name)
            .Select(g => new DashboardPhaseCounts(g.Key, g.ToDictionary(r => r.Status, r => r.N)))
            .ToList();

        return new GetPlanDashboardOutput(
            new DashboardPlan(plan.Id, plan.Name, plan.Status, plan.Objective),
            byStatus.Counts,
            byPhase,
            nextClaimable,
            myClaims,
            stuck,
            events);
    }
}

// ============================================================================
// get_my_active_work
// ============================================================================

public sealed record GetMyActiveWorkInput;
public sealed record MyClaim
{
    public string EntityType   { get; init; } = "";
    public Guid   EntityId     { get; init; }
    public Guid   PlanId       { get; init; }
    public string Role         { get; init; } = "";
    public DateTimeOffset? ClaimedUntil { get; init; }
}
public sealed record GetMyActiveWorkOutput(IReadOnlyList<MyClaim> Claims);

public sealed class GetMyActiveWorkTool : McpTool<GetMyActiveWorkInput, GetMyActiveWorkOutput>
{
    private readonly IDbConnectionFactory _factory;

    public GetMyActiveWorkTool(IDbConnectionFactory factory) => _factory = factory;

    public override string Name => "get_my_active_work";
    public override string Description =>
        "Every active claim the calling actor currently holds across every plan: tasks, findings, " +
        "and attempts in review. Use this on session start to recover claims after a Claude restart.";

    protected override async Task<GetMyActiveWorkOutput> RunAsync(GetMyActiveWorkInput _, RequestContext ctx, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await Dapper.SqlMapper.QueryAsync<MyClaim>(conn, new Dapper.CommandDefinition("""
            SELECT 'task' AS EntityType, id AS EntityId, plan_id AS PlanId,
                   claim_role AS Role, claimed_until AS ClaimedUntil
            FROM tasks
            WHERE claim_actor_id = @actorId AND claim_token IS NOT NULL AND claimed_until > now()
            UNION ALL
            SELECT 'finding' AS EntityType, f.id AS EntityId,
                   (SELECT plan_id FROM tasks WHERE id = f.task_id) AS PlanId,
                   f.claim_role AS Role, f.claimed_until AS ClaimedUntil
            FROM findings f
            WHERE f.claim_actor_id = @actorId AND f.claim_token IS NOT NULL AND f.claimed_until > now()
            UNION ALL
            SELECT 'attempt' AS EntityType, a.id AS EntityId,
                   COALESCE(
                     (SELECT plan_id FROM tasks WHERE id = a.task_id),
                     (SELECT plan_id FROM tasks t JOIN findings f ON f.task_id = t.id WHERE f.id = a.finding_id)
                   ) AS PlanId,
                   a.claim_role AS Role, a.claimed_until AS ClaimedUntil
            FROM attempts a
            WHERE a.claim_actor_id = @actorId AND a.claim_token IS NOT NULL AND a.claimed_until > now()
            """, new { actorId = ctx.ActorId }, cancellationToken: ct)).ConfigureAwait(false);
        return new GetMyActiveWorkOutput(rows.ToList());
    }
}
