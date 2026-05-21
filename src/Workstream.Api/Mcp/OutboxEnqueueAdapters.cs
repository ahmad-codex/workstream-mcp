using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.StateMachine;
using Workstream.Data.Repositories;
using Workstream.Mcp.Notifications;
using Workstream.Slack;

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

    public Task EnqueueAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus, string? assigneeGithubUsername = null, CancellationToken ct = default)
        => _outbox.EnqueueBoardSyncAsync(taskId, boardId, targetColumn, targetStatus, assigneeGithubUsername, ct);
}

/// <summary>
/// Default <see cref="ISlackNotifyEnqueue"/>: formats the body from the plan-type's
/// <c>slack_templates</c> (config JSON) and writes a row to <c>slack_notify_log</c>. The
/// <c>Workstream.Slack.SlackNotifyWorker</c> drains and posts.
///
/// Every body gets a computed <c>{actor_line}</c> header — the acting role's persona,
/// role label, and an AI Agent / Human tag — plus an attachment colour keyed to the
/// role. A human actor's identity instead goes to the attachment author row, where it
/// carries the person's real GitHub avatar. Task and finding bodies are clickable:
/// tasks deep-link to their bound Project V2 card, findings deep-link to the parent
/// task's card. Tokens the caller did not supply are stripped, never left as a literal
/// <c>{placeholder}</c>.
/// </summary>
public sealed class OutboxSlackNotifyEnqueue : ISlackNotifyEnqueue
{
    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;

    public OutboxSlackNotifyEnqueue(IOutboxRepository outbox, IProjectRepository projects, ITaskRepository tasks)
    {
        _outbox = outbox; _projects = projects; _tasks = tasks;
    }

    public async Task EnqueueForTaskAsync(
        Plan plan, (PlanType Row, StateGraph Graph) pt, WorkTask task,
        string notificationType, RequestContext ctx,
        IReadOnlyDictionary<string, string>? extraTokens = null,
        CancellationToken ct = default)
    {
        var channel = await ResolveChannelAsync(plan, ct).ConfigureAwait(false);
        if (channel is null) return;

        // Build the clickable title token. With a board AND a per-item databaseId we
        // deep-link to the project's side-pane view for the exact card; without an item
        // number we link to the board page; without a board we just bold the title.
        var (boardUrl, itemUrl) = await ResolveTaskLinkUrlsAsync(plan, task.Id, ct).ConfigureAwait(false);
        var linkUrl = itemUrl ?? boardUrl;
        var titleLink = linkUrl is null ? $"*{SlackEscape(task.Title)}*" : $"<{linkUrl}|{SlackEscape(task.Title)}>";

        // Persona name keyed by the task id so every post for this task shows the
        // same agent name while parallel tasks read as different agents.
        var identity = ResolveActorIdentity(pt.Row, notificationType, ctx, task.Id);

        var tokens = new Dictionary<string, string>
        {
            ["actor_line"]       = identity.ActorLine,
            ["actor"]            = ctx.DisplayActor,
            ["task_title"]       = task.Title,
            ["task_title_link"]  = titleLink,
            ["task_id"]          = task.Id.ToString(),
            ["status"]           = task.Status,
            ["description"]      = task.Description ?? "",
            ["priority"]         = task.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["external_key"]     = task.ExternalKey,
            ["board_url"]        = boardUrl ?? "",
            ["item_url"]         = itemUrl ?? boardUrl ?? "",
        };
        // Caller-supplied extras override defaults (e.g. commit / reviewer / reason /
        // finding_count injected by submit_review_decision, submit_findings, mark_task_status).
        if (extraTokens is not null)
            foreach (var kv in extraTokens) tokens[kv.Key] = kv.Value;

        var body = FormatTemplate(pt.Row, notificationType, tokens);

        // Rich Block Kit payload: a header, a Project / Plan / Task / Status field grid,
        // the persona context line, and deep-link buttons. The plain body stays as the
        // attachment fallback. Built from the same data the body tokens carry.
        var project = await _projects.GetAsync(plan.ProjectId, ct).ConfigureAwait(false);
        var blocks = BuildTaskBlocks(project?.DisplayName, plan.ProjectId, plan.Name,
            notificationType, titleLink, task, identity, extraTokens);

        // Per operator preference, each state change is its own top-level post (no
        // thread reply). The spec's threaded design (§8.4) is preserved as data — the
        // first slack_ts is still recorded on slack_notify_log.
        await _outbox.EnqueueSlackAsync(new SlackNotifyRow
        {
            ProjectId = plan.ProjectId, PlanId = plan.Id,
            EntityType = EntityType.Task, EntityId = task.Id,
            ChannelId = channel, NotificationType = notificationType,
            Body = body, BlocksJson = blocks, Color = identity.Color,
            AuthorName = identity.AuthorName, AuthorIcon = identity.AuthorIcon, AuthorLink = identity.AuthorLink,
            NextAttemptAt = DateTime.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    public async Task EnqueueForFindingAsync(Plan plan, (PlanType Row, StateGraph Graph) pt, Finding finding, Guid taskId, string notificationType, RequestContext ctx, IReadOnlyDictionary<string, string>? extraTokens = null, CancellationToken ct = default)
    {
        var channel = await ResolveChannelAsync(plan, ct).ConfigureAwait(false);
        if (channel is null) return;

        // A finding has no board card of its own — deep-link to the parent task's card
        // so operators can click straight through to where the audit work lives.
        var task = await _tasks.GetAsync(taskId, ct).ConfigureAwait(false);
        var (boardUrl, itemUrl) = await ResolveTaskLinkUrlsAsync(plan, taskId, ct).ConfigureAwait(false);
        var linkUrl = itemUrl ?? boardUrl;

        var taskTitle = task?.Title ?? "the audit task";
        var taskTitleLink = linkUrl is null ? $"*{SlackEscape(taskTitle)}*" : $"<{linkUrl}|{SlackEscape(taskTitle)}>";

        var summary = ShortSummary(finding.Symptom);
        var findingLabel = summary.Length == 0 ? finding.ExternalKey : $"{finding.ExternalKey} — {summary}";
        var findingLink = linkUrl is null ? $"*{SlackEscape(findingLabel)}*" : $"<{linkUrl}|{SlackEscape(findingLabel)}>";

        // Persona name keyed by the finding id — each finding's verifier / fixer /
        // fix-verifier reads as its own agent across parallel findings.
        var identity = ResolveActorIdentity(pt.Row, notificationType, ctx, finding.Id);

        var tokens = new Dictionary<string, string>
        {
            ["actor_line"]      = identity.ActorLine,
            ["actor"]           = ctx.DisplayActor,
            ["finding_key"]     = finding.ExternalKey,
            ["finding_summary"] = summary,
            ["finding_link"]    = findingLink,
            ["severity"]        = finding.Severity ?? "unknown",
            ["severity_emoji"]  = SeverityEmoji(finding.Severity),
            ["task_title"]      = taskTitle,
            ["task_title_link"] = taskTitleLink,
            ["task_id"]         = taskId.ToString(),
        };
        // Caller-supplied extras (reason, attempt, commit) overlay the defaults so the
        // verdict tools and override_verdict can surface a real explanation.
        if (extraTokens is not null)
            foreach (var kv in extraTokens) tokens[kv.Key] = kv.Value;

        var body = FormatTemplate(pt.Row, notificationType, tokens);

        // Rich Block Kit payload — finding deep-links to the parent audit task's card.
        var project = await _projects.GetAsync(plan.ProjectId, ct).ConfigureAwait(false);
        var blocks = BuildFindingBlocks(project?.DisplayName, plan.ProjectId, plan.Name,
            notificationType, findingLink, taskTitleLink, finding, identity, extraTokens);

        await _outbox.EnqueueSlackAsync(new SlackNotifyRow
        {
            ProjectId = plan.ProjectId, PlanId = plan.Id,
            EntityType = EntityType.Finding, EntityId = finding.Id,
            ChannelId = channel, NotificationType = notificationType,
            Body = body, BlocksJson = blocks, Color = identity.Color,
            AuthorName = identity.AuthorName, AuthorIcon = identity.AuthorIcon, AuthorLink = identity.AuthorLink,
            NextAttemptAt = DateTime.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    public async Task EnqueueForPlanAsync(Plan plan, (PlanType Row, StateGraph Graph) pt, string notificationType, RequestContext ctx, CancellationToken ct = default)
    {
        var channel = await ResolveChannelAsync(plan, ct).ConfigureAwait(false);
        if (channel is null) return;
        var project = await _projects.GetAsync(plan.ProjectId, ct).ConfigureAwait(false);
        var body = FormatTemplate(pt.Row, notificationType, new Dictionary<string, string>
        {
            ["actor"]     = ctx.DisplayActor,
            ["plan_name"] = plan.Name,
            ["project"]   = project?.DisplayName ?? plan.ProjectId.ToString(),
        });
        var blocks = BuildPlanBlocks(project?.DisplayName, plan, pt.Row, notificationType, ctx);
        await _outbox.EnqueueSlackAsync(new SlackNotifyRow
        {
            ProjectId = plan.ProjectId, PlanId = plan.Id,
            EntityType = EntityType.Plan, EntityId = plan.Id,
            ChannelId = channel, NotificationType = notificationType,
            Body = body, BlocksJson = blocks,
            NextAttemptAt = DateTime.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the Block Kit payload for a plan notification: a header, a
    /// Project / Plan / Type / Status field grid, the plan objective as the body, and a
    /// <c>View details</c> button. Keeps plan posts as informative as task / finding ones.
    /// </summary>
    private static string BuildPlanBlocks(
        string? projectName, Plan plan, PlanType planType, string notificationType, RequestContext ctx)
    {
        var fields = new List<SlackBlockKitBuilder.Field>
        {
            new("Project", projectName ?? "—"),
            new("Plan", plan.Name),
            new("Type", string.IsNullOrWhiteSpace(planType.DisplayName) ? plan.PlanTypeId : planType.DisplayName),
            new("Status", Humanize(plan.Status)),
        };
        var context = string.IsNullOrWhiteSpace(ctx.DisplayActor)
            ? null : $":bust_in_silhouette: {ctx.DisplayActor}";
        return SlackBlockKitBuilder.Build(
            HeaderFor(notificationType, null),
            fields, context,
            string.IsNullOrWhiteSpace(plan.Objective) ? null : plan.Objective!.Trim(),
            DetailsButton(EntityType.Plan, plan.ProjectId, plan.Id));
    }

    private async Task<string?> ResolveChannelAsync(Plan plan, CancellationToken ct)
    {
        if (plan.PrimarySlackChannelId is { Length: > 0 } overrideCh) return overrideCh;
        var slack = await _projects.GetSlackAsync(plan.ProjectId, ct).ConfigureAwait(false);
        return slack?.DefaultChannelId;
    }

    /// <summary>
    /// Resolve the (board-page, per-item pane) URL pair for a task's bound Project V2
    /// card. Either may be null: no board bound, or the item not created yet.
    /// </summary>
    private async Task<(string? BoardUrl, string? ItemUrl)> ResolveTaskLinkUrlsAsync(Plan plan, Guid taskId, CancellationToken ct)
    {
        if (plan.PrimaryBoardId is not { } boardId) return (null, null);
        var board = await _projects.GetBoardAsync(boardId, ct).ConfigureAwait(false);
        if (board is null) return (null, null);
        var boardUrl = $"https://github.com/orgs/{board.GithubOwner}/projects/{board.GithubProjectNumber}";
        var itemNumber = await _tasks.GetGithubBoardItemNumberAsync(taskId, ct).ConfigureAwait(false);
        var itemUrl = itemNumber is { } n ? $"{boardUrl}/views/1?pane=issue&itemId={n}" : null;
        return (boardUrl, itemUrl);
    }

    /// <summary>
    /// Build the message identity: the in-body <c>{actor_line}</c> header, the attachment
    /// colour, and the attachment author row. The role is the caller's declared
    /// <see cref="RequestContext.ActingRole"/> (the <c>as_agent</c> payload field) when
    /// present, otherwise resolved from the plan-type's <c>slack_roles</c> map; the
    /// persona, emoji and colour come from <c>role_personas</c>.
    ///
    /// A human actor's identity moves to the attachment author row so it can carry the
    /// person's real GitHub avatar (<c>github.com/&lt;login&gt;.png</c>); their in-body
    /// actor line is left empty. An AI actor — or a human who declared an <c>as_agent</c>
    /// role for the call — keeps the role-persona emoji line in the body and has no
    /// author row (the personas are not GitHub accounts).
    /// </summary>
    private static ActorIdentity ResolveActorIdentity(
        PlanType pt, string notificationType, RequestContext ctx, Guid personaKey)
    {
        string? persona = null, label = null, emoji = null, color = null;
        // An explicit as_agent role wins; otherwise derive the role from the
        // notification type via slack_roles.
        var role = string.IsNullOrWhiteSpace(ctx.ActingRole) ? null : ctx.ActingRole!.Trim();
        try
        {
            using var doc = JsonDocument.Parse(pt.ConfigJson);
            var root = doc.RootElement;
            if (role is null
                && root.TryGetProperty("slack_roles", out var roles) && roles.ValueKind == JsonValueKind.Object
                && roles.TryGetProperty(notificationType, out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
            {
                role = roleEl.GetString();
            }
            if (role is not null
                && root.TryGetProperty("role_personas", out var personas) && personas.ValueKind == JsonValueKind.Object
                && personas.TryGetProperty(role, out var p) && p.ValueKind == JsonValueKind.Object)
            {
                persona = PickPersona(p, personaKey);
                label   = GetStr(p, "label");
                emoji   = GetStr(p, "emoji");
                color   = GetStr(p, "color");
            }
        }
        catch (JsonException) { /* fall through to a generic identity */ }

        // A caller that declared an as_agent role renders as an AI agent even on a
        // human token — attribution follows the declared role, not actor_type.
        var actingAsAgent = !string.IsNullOrWhiteSpace(ctx.ActingRole);
        var isHuman = !actingAsAgent && string.Equals(ctx.ActorType, "human", StringComparison.OrdinalIgnoreCase);
        var kind = isHuman ? "Human" : "AI Agent";

        // Escalations and failures get a red bar even with no role persona; anything
        // else still uncoloured falls back to neutral grey so every task/finding post
        // is an attachment and the author row can render.
        color ??= notificationType switch
        {
            "task.blocked" or "task.needs_human_review"
                or "finding.needs_human_review" or "fix.failed" => "#DC2626",
            _ => "#6B7280",
        };

        if (isHuman)
        {
            // The person's identity becomes the attachment author row, with their real
            // GitHub avatar. The in-body actor line is emptied (TidyTemplate drops it).
            var roleSuffix = string.IsNullOrEmpty(label) ? "" : $" · {label}";
            var authorName = $"{ctx.DisplayActor}{roleSuffix} · {kind}";
            string? authorIcon = null, authorLink = null;
            if (!string.IsNullOrWhiteSpace(ctx.GithubUsername))
            {
                var login = Uri.EscapeDataString(ctx.GithubUsername);
                authorIcon = $"https://github.com/{login}.png?size=64";
                authorLink = $"https://github.com/{login}";
            }
            return new ActorIdentity("", color, authorName, authorIcon, authorLink);
        }

        // AI actor: keep the role-persona emoji line inline in the body; no avatar.
        var actorLine = persona is not null && emoji is not null && label is not null
            ? $"{emoji} *{persona}* · {label} · _{kind}_"
            : $":robot_face: *{ctx.DisplayActor}* · _{kind}_";
        return new ActorIdentity(actorLine, color, null, null, null);
    }

    private static string? GetStr(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>
    /// Pick the persona name for a role. When the role carries a <c>personas</c> name
    /// pool, the name is chosen deterministically from <paramref name="key"/> (the work
    /// item's id) — so every post for the same task/finding shows the same name, while
    /// parallel work items land on different names and read as distinct agents. Falls
    /// back to a single <c>persona</c> string for backward compatibility.
    /// </summary>
    private static string? PickPersona(JsonElement roleObj, Guid key)
    {
        if (roleObj.TryGetProperty("personas", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                    names.Add(s);
            if (names.Count > 0)
            {
                // Stable, process-independent hash of the guid (first 4 bytes).
                var h = BitConverter.ToUInt32(key.ToByteArray(), 0);
                return names[(int)(h % (uint)names.Count)];
            }
        }
        return GetStr(roleObj, "persona");
    }

    private static string SeverityEmoji(string? severity) => (severity ?? "").ToLowerInvariant() switch
    {
        "critical" => ":small_red_triangle:",
        "high"     => ":small_orange_diamond:",
        "medium"   => ":small_blue_diamond:",
        "low"      => ":white_small_square:",
        _          => ":small_red_triangle:",
    };

    /// <summary>
    /// Build the Block Kit payload for a task notification: a header, a
    /// Project / Plan / Task / Status field grid, an optional description / reason /
    /// commit body, the persona context line (blank for human actors), and a
    /// <c>View details</c> button that opens the detail modal (Part B).
    /// </summary>
    private static string BuildTaskBlocks(
        string? projectName, Guid projectId, string planName, string notificationType,
        string titleLink, WorkTask task, ActorIdentity identity,
        IReadOnlyDictionary<string, string>? extras)
    {
        var fields = new List<SlackBlockKitBuilder.Field>
        {
            new("Project", projectName ?? "—"),
            new("Plan", planName),
            new("Task", titleLink),
            new("Status", Humanize(task.Status)),
        };

        var body = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.Description)) body.Add(task.Description!.Trim());
        if (extras is not null)
        {
            if (extras.TryGetValue("reason", out var r) && !string.IsNullOrWhiteSpace(r))
                body.Add($"*Reason:* {r}");
            if (extras.TryGetValue("commit", out var c) && !string.IsNullOrWhiteSpace(c))
                body.Add($"*Commit:* `{c}`");
        }

        return SlackBlockKitBuilder.Build(
            HeaderFor(notificationType, null),
            fields, ContextLine(identity),
            body.Count > 0 ? string.Join("\n\n", body) : null,
            DetailsButton(EntityType.Task, projectId, task.Id));
    }

    /// <summary>
    /// Build the Block Kit payload for a finding notification: a header carrying the
    /// severity emoji, a Project / Plan / Finding / Severity / Audit-task field grid,
    /// the symptom and any verdict reason as the body, and a <c>View details</c> button.
    /// </summary>
    private static string BuildFindingBlocks(
        string? projectName, Guid projectId, string planName, string notificationType,
        string findingLink, string taskTitleLink, Finding finding,
        ActorIdentity identity, IReadOnlyDictionary<string, string>? extras)
    {
        var severity = string.IsNullOrWhiteSpace(finding.Severity)
            ? "—"
            : $"{SeverityEmoji(finding.Severity)} {Humanize(finding.Severity!)}";

        var fields = new List<SlackBlockKitBuilder.Field>
        {
            new("Project", projectName ?? "—"),
            new("Plan", planName),
            new("Finding", findingLink),
            new("Severity", severity),
            new("Audit task", taskTitleLink),
        };

        var body = new List<string>();
        if (!string.IsNullOrWhiteSpace(finding.Symptom)) body.Add(finding.Symptom!.Trim());
        if (extras is not null && extras.TryGetValue("reason", out var r) && !string.IsNullOrWhiteSpace(r))
            body.Add($"*Reason:* {r}");

        return SlackBlockKitBuilder.Build(
            HeaderFor(notificationType, SeverityEmoji(finding.Severity)),
            fields, ContextLine(identity),
            body.Count > 0 ? string.Join("\n\n", body) : null,
            DetailsButton(EntityType.Finding, projectId, finding.Id));
    }

    /// <summary>
    /// The card's context line. An AI actor keeps its role-persona line; a human actor —
    /// whose identity used to ride on the (now dropped) attachment author row — is
    /// rendered inline so block-based cards still attribute the person.
    /// </summary>
    private static string? ContextLine(ActorIdentity identity)
        => !string.IsNullOrEmpty(identity.ActorLine) ? identity.ActorLine
           : identity.AuthorName is { Length: > 0 } name ? $":bust_in_silhouette: {name}"
           : null;

    /// <summary>
    /// The single <c>View details</c> action button. Its <c>value</c> carries
    /// <c>entityType|projectId|entityId</c> so the interactivity endpoint (Part B) can
    /// load the entity and open the detail modal. Until that endpoint ships the button
    /// renders but the click has no handler.
    /// </summary>
    private static IReadOnlyList<SlackBlockKitBuilder.Button> DetailsButton(
        string entityType, Guid projectId, Guid entityId)
        => new[]
        {
            SlackBlockKitBuilder.Button.Action(
                ":mag: View details", "view_details",
                $"{entityType}|{projectId}|{entityId}", "primary"),
        };

    /// <summary>The Block Kit header line — an emoji plus a human-readable event name.</summary>
    private static string HeaderFor(string notificationType, string? severityEmoji) => notificationType switch
    {
        "task.created"               => ":new: Task created",
        "task.claimed"               => ":inbox_tray: Task claimed",
        "task.in_progress"           => ":hourglass_flowing_sand: Task in progress",
        "task.review"                => ":mag: Task in review",
        "task.done"                  => ":white_check_mark: Task done",
        "task.blocked"               => ":no_entry: Task blocked",
        "task.needs_human_review"    => ":raising_hand: Task needs human review",
        "finding.confirmed"          => $"{severityEmoji ?? ":white_check_mark:"} Finding confirmed",
        "finding.rejected"           => ":x: Finding rejected",
        "finding.ambiguous"          => ":grey_question: Finding ambiguous",
        "finding.needs_human_review" => ":raising_hand: Finding needs human review",
        "finding.deferred"           => ":double_vertical_bar: Finding deferred",
        "fix.confirmed"              => ":white_check_mark: Fix confirmed",
        "fix.failed"                 => ":x: Fix failed",
        "fix.partial"                => ":large_yellow_circle: Fix partial",
        "plan.activated"             => ":rocket: Plan activated",
        "plan.completed"             => ":checkered_flag: Plan completed",
        "plan.archived"              => ":file_cabinet: Plan archived",
        _                            => Humanize(notificationType),
    };

    /// <summary>Turn a dotted / underscored token (status, event type) into Title Case words.</summary>
    private static string Humanize(string s)
    {
        var words = s.Replace('.', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();
        return words.Length == 0 ? s : string.Join(' ', words);
    }

    /// <summary>Collapse a finding symptom to a single short line for a Slack link label.</summary>
    private static string ShortSummary(string? text, int max = 90)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Regex.Replace(text.Trim(), @"\s+", " ");
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Escape characters that would break Slack mrkdwn link syntax (&lt;url|text&gt;).</summary>
    private static string SlackEscape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "∣");

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
        return TidyTemplate(sb.ToString());
    }

    /// <summary>
    /// Remove any token the caller did not supply (so a missing value can never surface
    /// as a literal "{reason}" in the channel) and clean up the dangling separators and
    /// empty lines that leaves behind — including the blank first line for a human actor,
    /// whose identity moved to the attachment author row.
    /// </summary>
    private static string TidyTemplate(string s)
    {
        // Drop unsubstituted {tokens}.
        s = Regex.Replace(s, @"\{[a-zA-Z_][a-zA-Z0-9_]*\}", "");

        var lines = s.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var line = raw;
            // Strip trailing dangling separators / empty labels (e.g. "— reason: ").
            for (var i = 0; i < 4; i++)
            {
                var trimmed = Regex.Replace(line, @"\s*(—|·|reason:|attempt|commit `?`?)\s*$", "",
                    RegexOptions.IgnoreCase);
                if (trimmed == line) break;
                line = trimmed;
            }
            // Drop a quote line that lost all of its content.
            if (Regex.IsMatch(line, @"^\s*>\s*(reason:)?\s*$", RegexOptions.IgnoreCase))
                continue;
            kept.Add(line.TrimEnd());
        }
        return string.Join('\n', kept).Trim();
    }

    /// <summary>The pieces of a notification's actor identity, resolved at enqueue time.</summary>
    private sealed record ActorIdentity(
        string ActorLine, string? Color, string? AuthorName, string? AuthorIcon, string? AuthorLink);
}
