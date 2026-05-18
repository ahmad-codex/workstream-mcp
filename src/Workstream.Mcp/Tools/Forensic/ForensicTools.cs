using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Data;
using Workstream.Data.Repositories;

namespace Workstream.Mcp.Tools.Forensic;

// ============================================================================
// get_event_log
// ============================================================================

public sealed record GetEventLogInput(string EntityType, Guid EntityId, DateTimeOffset? Since = null, int Limit = 100);
public sealed record EventLogEntry(long Id, DateTimeOffset At, Guid? ActorId, string EventType, string? FromState, string? ToState, string Payload);
public sealed record GetEventLogOutput(IReadOnlyList<EventLogEntry> Events);

public sealed class GetEventLogTool : McpTool<GetEventLogInput, GetEventLogOutput>
{
    private readonly IEventRepository _events;

    public GetEventLogTool(IEventRepository events) => _events = events;

    public override string Name => "get_event_log";
    public override string Description =>
        "Forensic event log for a specific entity (task / finding / attempt / plan / phase). Used to " +
        "investigate 'why did this task end up in needs_human_review' — the events table records every " +
        "state change with the actor and reason.";

    protected override async Task<GetEventLogOutput> RunAsync(GetEventLogInput input, RequestContext ctx, CancellationToken ct)
    {
        var events = await _events.ListForEntityAsync(input.EntityType, input.EntityId, input.Since, input.Limit, ct).ConfigureAwait(false);
        return new GetEventLogOutput(events
            .Select(e => new EventLogEntry(e.Id, e.At, e.ActorId, e.EventType, e.FromState, e.ToState, e.Payload))
            .ToList());
    }
}

// ============================================================================
// get_stuck_work
// ============================================================================

public sealed record GetStuckWorkInput(Guid? PlanId = null);
public sealed record StuckItem
{
    public string EntityType   { get; init; } = "";
    public Guid   EntityId     { get; init; }
    public string Status       { get; init; } = "";
    public DateTimeOffset Since { get; init; }
    public string? ClaimHolder { get; init; }
    public string  Reason      { get; init; } = "";
}
public sealed record GetStuckWorkOutput(IReadOnlyList<StuckItem> Items);

public sealed class GetStuckWorkTool : McpTool<GetStuckWorkInput, GetStuckWorkOutput>
{
    private readonly IDbConnectionFactory _factory;

    public GetStuckWorkTool(IDbConnectionFactory factory) => _factory = factory;

    public override string Name => "get_stuck_work";
    public override string Description =>
        "On-demand view of the same data the hourly stuck-work job computes: tasks past their TTL, " +
        "findings in pending_verification > 24h, attempts past TTL, plans with no events in the last " +
        "24h. Scoped to a single plan or global.";

    protected override async Task<GetStuckWorkOutput> RunAsync(GetStuckWorkInput input, RequestContext ctx, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = """
            SELECT 'task' AS EntityType,
                   t.id AS EntityId,
                   t.status AS Status,
                   t.updated_at AS Since,
                   (SELECT github_username FROM users u WHERE u.id = t.claim_actor_id) AS ClaimHolder,
                   'claim expired or stale claim' AS Reason
            FROM tasks t
            WHERE (@planId IS NULL OR t.plan_id = @planId)
              AND t.claim_token IS NOT NULL
              AND t.claimed_until < now() - interval '15 minutes'
            UNION ALL
            SELECT 'finding', f.id, f.status, f.updated_at,
                   (SELECT github_username FROM users u WHERE u.id = f.claim_actor_id),
                   'pending verification > 24h'
            FROM findings f JOIN tasks t ON t.id = f.task_id
            WHERE (@planId IS NULL OR t.plan_id = @planId)
              AND f.status = 'pending_verification'
              AND f.created_at < now() - interval '24 hours'
            ORDER BY Since
            """;
        var rows = await Dapper.SqlMapper.QueryAsync<StuckItem>(conn, new Dapper.CommandDefinition(sql,
            new { planId = input.PlanId }, cancellationToken: ct)).ConfigureAwait(false);
        return new GetStuckWorkOutput(rows.ToList());
    }
}

// ============================================================================
// export_plan
// ============================================================================

public sealed record ExportPlanInput(Guid PlanId, string Format = "markdown");
public sealed record ExportPlanOutput(string Format, string Content);

public sealed class ExportPlanTool : McpTool<ExportPlanInput, ExportPlanOutput>
{
    private readonly IPlanRepository _plans;
    private readonly IDbConnectionFactory _factory;

    public ExportPlanTool(IPlanRepository plans, IDbConnectionFactory factory)
    {
        _plans = plans; _factory = factory;
    }

    public override string Name => "export_plan";
    public override string Description =>
        "Export a deterministic snapshot of a plan, suitable for committing to a repo or pasting into " +
        "chat. format ∈ {markdown, json}. Markdown is human-readable and stable; JSON is the full " +
        "structured tree (tasks, findings, attempts, verdicts). Export is one-way — the database is " +
        "the source of truth, not the markdown.";

    protected override async Task<ExportPlanOutput> RunAsync(ExportPlanInput input, RequestContext ctx, CancellationToken ct)
    {
        var plan = await _plans.GetAsync(input.PlanId, ct).ConfigureAwait(false)
                   ?? throw new WorkstreamException(WorkstreamError.NotFound("plan"));
        if (input.Format == "json")
        {
            // JSON export: gather all entities, serialize.
            await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
            var tasks    = await Dapper.SqlMapper.QueryAsync(conn, new Dapper.CommandDefinition(
                "SELECT * FROM tasks WHERE plan_id = @id", new { id = plan.Id }, cancellationToken: ct))
                .ConfigureAwait(false);
            var findings = await Dapper.SqlMapper.QueryAsync(conn, new Dapper.CommandDefinition(
                "SELECT f.* FROM findings f JOIN tasks t ON t.id = f.task_id WHERE t.plan_id = @id", new { id = plan.Id }, cancellationToken: ct))
                .ConfigureAwait(false);
            var attempts = await Dapper.SqlMapper.QueryAsync(conn, new Dapper.CommandDefinition(
                "SELECT a.* FROM attempts a LEFT JOIN findings f ON f.id = a.finding_id LEFT JOIN tasks t ON t.id = COALESCE(a.task_id, f.task_id) WHERE t.plan_id = @id",
                new { id = plan.Id }, cancellationToken: ct))
                .ConfigureAwait(false);
            var payload = new
            {
                plan,
                tasks = tasks.Cast<object>().ToList(),
                findings = findings.Cast<object>().ToList(),
                attempts = attempts.Cast<object>().ToList(),
            };
            return new ExportPlanOutput("json", System.Text.Json.JsonSerializer.Serialize(payload));
        }

        // Markdown export.
        var sb = new StringBuilder();
        sb.AppendLine($"# {plan.Name}");
        sb.AppendLine();
        sb.AppendLine($"_Plan id: `{plan.Id}` · type: `{plan.PlanTypeId}` · status: **{plan.Status}**_");
        if (plan.Objective is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(plan.Objective);
        }
        sb.AppendLine();
        sb.AppendLine("## Tasks");
        await using (var conn = await _factory.OpenAsync(ct).ConfigureAwait(false))
        {
            var rows = await Dapper.SqlMapper.QueryAsync<(string ExternalKey, string Title, string Status, int Priority)>(
                conn, new Dapper.CommandDefinition(
                "SELECT external_key, title, status, priority FROM tasks WHERE plan_id = @id ORDER BY priority DESC, created_at",
                new { id = plan.Id }, cancellationToken: ct)).ConfigureAwait(false);
            foreach (var r in rows)
            {
                sb.AppendLine($"- `{r.ExternalKey}` (p{r.Priority}) **{r.Status}** — {r.Title}");
            }
        }
        return new ExportPlanOutput("markdown", sb.ToString());
    }
}
