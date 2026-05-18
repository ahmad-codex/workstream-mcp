using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.StateMachine;
using Workstream.Data.Repositories;
using Workstream.Mcp.Notifications;

namespace Workstream.Api.Mcp;

/// <summary>
/// Default <see cref="IBoardSyncEnqueue"/>: writes a row to <c>board_sync_log</c>. The
/// <c>Workstream.GitHub.BoardSyncWorker</c> drains the queue (§7.5). Tools that only use
/// this enqueue interface stay decoupled from GitHub.
/// </summary>
public sealed class OutboxBoardSyncEnqueue : IBoardSyncEnqueue
{
    private readonly IOutboxRepository _outbox;
    public OutboxBoardSyncEnqueue(IOutboxRepository outbox) => _outbox = outbox;

    public Task EnqueueAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus, CancellationToken ct = default)
        => _outbox.EnqueueBoardSyncAsync(taskId, boardId, targetColumn, targetStatus, ct);
}

/// <summary>
/// Default <see cref="ISlackNotifyEnqueue"/>: formats the body from the plan-type's
/// <c>slack_templates</c> (config JSON) and writes a row to <c>slack_notify_log</c>. The
/// <c>Workstream.Slack.SlackNotifyWorker</c> drains and posts.
/// </summary>
public sealed class OutboxSlackNotifyEnqueue : ISlackNotifyEnqueue
{
    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;

    public OutboxSlackNotifyEnqueue(IOutboxRepository outbox, IProjectRepository projects)
    {
        _outbox = outbox; _projects = projects;
    }

    public async Task EnqueueForTaskAsync(Plan plan, (PlanType Row, StateGraph Graph) pt, WorkTask task, string notificationType, RequestContext ctx, CancellationToken ct = default)
    {
        var channel = await ResolveChannelAsync(plan, ct).ConfigureAwait(false);
        if (channel is null) return;
        var body = FormatTemplate(pt.Row, notificationType, new Dictionary<string, string>
        {
            ["actor"]      = ctx.GithubUsername,
            ["task_title"] = task.Title,
            ["task_id"]    = task.Id.ToString(),
            ["status"]     = task.Status,
        });
        var threadTs = await _outbox.GetParentSlackTsAsync(EntityType.Task, task.Id, ct).ConfigureAwait(false);
        await _outbox.EnqueueSlackAsync(new SlackNotifyRow(
            Id: 0, EventId: null, ProjectId: plan.ProjectId, PlanId: plan.Id,
            EntityType: EntityType.Task, EntityId: task.Id,
            ChannelId: channel, NotificationType: notificationType,
            Body: body, ThreadTs: threadTs, SlackTs: null,
            Attempts: 0, NextAttemptAt: DateTimeOffset.UtcNow, Result: "pending"), ct).ConfigureAwait(false);
    }

    public async Task EnqueueForFindingAsync(Plan plan, (PlanType Row, StateGraph Graph) pt, Finding finding, Guid taskId, string notificationType, RequestContext ctx, CancellationToken ct = default)
    {
        var channel = await ResolveChannelAsync(plan, ct).ConfigureAwait(false);
        if (channel is null) return;
        var body = FormatTemplate(pt.Row, notificationType, new Dictionary<string, string>
        {
            ["actor"]       = ctx.GithubUsername,
            ["finding_key"] = finding.ExternalKey,
            ["severity"]    = finding.Severity ?? "unknown",
        });
        var threadTs = await _outbox.GetParentSlackTsAsync(EntityType.Task, taskId, ct).ConfigureAwait(false);
        await _outbox.EnqueueSlackAsync(new SlackNotifyRow(
            Id: 0, EventId: null, ProjectId: plan.ProjectId, PlanId: plan.Id,
            EntityType: EntityType.Finding, EntityId: finding.Id,
            ChannelId: channel, NotificationType: notificationType,
            Body: body, ThreadTs: threadTs, SlackTs: null,
            Attempts: 0, NextAttemptAt: DateTimeOffset.UtcNow, Result: "pending"), ct).ConfigureAwait(false);
    }

    public async Task EnqueueForPlanAsync(Plan plan, (PlanType Row, StateGraph Graph) pt, string notificationType, RequestContext ctx, CancellationToken ct = default)
    {
        var channel = await ResolveChannelAsync(plan, ct).ConfigureAwait(false);
        if (channel is null) return;
        var body = FormatTemplate(pt.Row, notificationType, new Dictionary<string, string>
        {
            ["actor"]     = ctx.GithubUsername,
            ["plan_name"] = plan.Name,
            ["project"]   = plan.ProjectId.ToString(),
        });
        await _outbox.EnqueueSlackAsync(new SlackNotifyRow(
            Id: 0, EventId: null, ProjectId: plan.ProjectId, PlanId: plan.Id,
            EntityType: EntityType.Plan, EntityId: plan.Id,
            ChannelId: channel, NotificationType: notificationType,
            Body: body, ThreadTs: null, SlackTs: null,
            Attempts: 0, NextAttemptAt: DateTimeOffset.UtcNow, Result: "pending"), ct).ConfigureAwait(false);
    }

    private async Task<string?> ResolveChannelAsync(Plan plan, CancellationToken ct)
    {
        if (plan.PrimarySlackChannelId is { Length: > 0 } overrideCh) return overrideCh;
        var slack = await _projects.GetSlackAsync(plan.ProjectId, ct).ConfigureAwait(false);
        return slack?.DefaultChannelId;
    }

    private static string FormatTemplate(PlanType pt, string notificationType, IReadOnlyDictionary<string, string> tokens)
    {
        try
        {
            using var doc = JsonDocument.Parse(pt.ConfigJson);
            if (doc.RootElement.TryGetProperty("slack_templates", out var t)
                && t.TryGetProperty(notificationType, out var tpl)
                && tpl.GetString() is { } template)
            {
                return SubstituteTokens(template, tokens);
            }
        }
        catch (JsonException) { /* fall through */ }
        // Fallback: notification_type + entity-type
        var sb = new StringBuilder();
        sb.Append(notificationType).Append(": ");
        foreach (var kv in tokens) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private static string SubstituteTokens(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var sb = new StringBuilder(template);
        foreach (var kv in tokens)
            sb.Replace("{" + kv.Key + "}", kv.Value);
        return sb.ToString();
    }
}
