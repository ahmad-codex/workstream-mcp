using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;
using Workstream.Slack;

namespace Workstream.Api.Webhooks;

/// <summary>
/// Inbound Slack interactivity webhook. When a user clicks the "View details" button on
/// a notification card, Slack POSTs a <c>block_actions</c> payload here; we verify the
/// signature, load the entity the button points at, and open a detail modal via
/// <c>views.open</c> within the ~3-second <c>trigger_id</c> window.
///
/// The Slack app must have Interactivity enabled with the Request URL set to
/// <c>https://&lt;host&gt;/webhooks/slack/interactivity</c>, and the app signing secret
/// must be configured (<c>WORKSTREAM_SLACK_SIGNING_SECRET</c>). The path is exempt from
/// the URL-token middleware (it matches the <c>/webhooks/</c> prefix); Slack's signature
/// is the auth.
/// </summary>
public static class SlackInteractivityController
{
    public static void MapSlackInteractivity(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/slack/interactivity", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        IOptions<SlackAppOptions> appOpts,
        IProjectRepository projects,
        IPlanRepository plans,
        ITaskRepository tasks,
        IFindingRepository findings,
        SlackClient slack,
        ISlackBotTokenResolver tokens,
        ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger("workstream.slack.interactivity");
        var ct = http.RequestAborted;

        // Read the raw body once — the HMAC must run over the exact bytes Slack signed.
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        var bytes = ms.ToArray();

        var timestamp = http.Request.Headers["X-Slack-Request-Timestamp"].ToString();
        var signature = http.Request.Headers["X-Slack-Signature"].ToString();
        if (!SlackSignatureVerifier.Verify(bytes, timestamp, signature, appOpts.Value.SigningSecret))
        {
            log.LogWarning("rejecting slack interaction with invalid signature");
            return Results.StatusCode(401);
        }

        // Past signature verification we always answer 200 — a non-200 makes Slack show
        // the user a generic error. Any handling failure is logged, not surfaced.
        try
        {
            var payloadJson = ExtractPayload(bytes);
            if (payloadJson is null) return Results.Ok();

            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (Str(root, "type") != "block_actions") return Results.Ok();

            var triggerId = Str(root, "trigger_id");
            var action = FirstAction(root);
            if (triggerId is null || action is null) return Results.Ok();
            if (Str(action.Value, "action_id") != "view_details") return Results.Ok();

            // value = "{entityType}|{projectId}|{entityId}"
            var value = Str(action.Value, "value") ?? "";
            var parts = value.Split('|');
            if (parts.Length != 3
                || !Guid.TryParse(parts[1], out var projectId)
                || !Guid.TryParse(parts[2], out var entityId))
            {
                log.LogWarning("slack interaction with unparseable value {Value}", value);
                return Results.Ok();
            }
            var entityType = parts[0];

            var slackCfg = await projects.GetSlackAsync(projectId, ct).ConfigureAwait(false);
            if (slackCfg is null)
            {
                log.LogWarning("slack interaction for project {Project} with no slack config", projectId);
                return Results.Ok();
            }

            var view = await BuildViewAsync(entityType, entityId, projects, plans, tasks, findings, ct)
                .ConfigureAwait(false);
            if (view is null)
            {
                log.LogWarning("slack interaction: {Type} {Id} not found", entityType, entityId);
                return Results.Ok();
            }

            var token = await tokens.ResolveAsync(slackCfg.BotTokenSecretRef, ct).ConfigureAwait(false);
            var result = await slack.OpenViewAsync(token, triggerId, view, ct).ConfigureAwait(false);
            if (!result.Ok)
                log.LogWarning("slack views.open rejected: {Error}", result.Error);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "slack interaction handling failed");
        }
        return Results.Ok();
    }

    // ----- payload parsing -----

    /// <summary>
    /// Pull the <c>payload</c> field out of the <c>application/x-www-form-urlencoded</c>
    /// body. Slack sends exactly <c>payload=&lt;url-encoded JSON&gt;</c>.
    /// </summary>
    private static string? ExtractPayload(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        foreach (var pair in text.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0 || pair[..eq] != "payload") continue;
            // Form encoding: '+' is a space, everything else is %-encoded.
            return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private static JsonElement? FirstAction(JsonElement root)
        => root.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array
            && a.GetArrayLength() > 0
            ? a[0]
            : null;

    private static string? Str(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    // ----- modal content -----

    private static async Task<string?> BuildViewAsync(
        string entityType, Guid entityId,
        IProjectRepository projects, IPlanRepository plans,
        ITaskRepository tasks, IFindingRepository findings,
        CancellationToken ct)
    {
        switch (entityType)
        {
            case EntityType.Task:
            {
                var task = await tasks.GetAsync(entityId, ct).ConfigureAwait(false);
                if (task is null) return null;
                var plan = await plans.GetAsync(task.PlanId, ct).ConfigureAwait(false);
                var project = plan is null ? null : await projects.GetAsync(plan.ProjectId, ct).ConfigureAwait(false);
                var boardUrl = await BoardUrlAsync(plan, entityId, projects, tasks, ct).ConfigureAwait(false);
                return BuildTaskModal(task, plan, project, boardUrl);
            }
            case EntityType.Finding:
            {
                var finding = await findings.GetAsync(entityId, ct).ConfigureAwait(false);
                if (finding is null) return null;
                var task = await tasks.GetAsync(finding.TaskId, ct).ConfigureAwait(false);
                var plan = task is null ? null : await plans.GetAsync(task.PlanId, ct).ConfigureAwait(false);
                var project = plan is null ? null : await projects.GetAsync(plan.ProjectId, ct).ConfigureAwait(false);
                var boardUrl = await BoardUrlAsync(plan, finding.TaskId, projects, tasks, ct).ConfigureAwait(false);
                return BuildFindingModal(finding, task, plan, project, boardUrl);
            }
            case EntityType.Plan:
            {
                var plan = await plans.GetAsync(entityId, ct).ConfigureAwait(false);
                if (plan is null) return null;
                var project = await projects.GetAsync(plan.ProjectId, ct).ConfigureAwait(false);
                return BuildPlanModal(plan, project);
            }
            default:
                return null;
        }
    }

    /// <summary>Resolve the GitHub Project V2 side-pane URL for a task's bound card, if any.</summary>
    private static async Task<string?> BoardUrlAsync(
        Plan? plan, Guid taskId, IProjectRepository projects, ITaskRepository tasks, CancellationToken ct)
    {
        if (plan?.PrimaryBoardId is not { } boardId) return null;
        var board = await projects.GetBoardAsync(boardId, ct).ConfigureAwait(false);
        if (board is null) return null;
        var baseUrl = $"https://github.com/orgs/{board.GithubOwner}/projects/{board.GithubProjectNumber}";
        var num = await tasks.GetGithubBoardItemNumberAsync(taskId, ct).ConfigureAwait(false);
        return num is { } n ? $"{baseUrl}/views/1?pane=issue&itemId={n}" : baseUrl;
    }

    private static string BuildTaskModal(WorkTask task, Plan? plan, Project? project, string? boardUrl)
    {
        var fields = new List<SlackBlockKitBuilder.Field>
        {
            new("Project", project?.DisplayName ?? "—"),
            new("Plan", plan?.Name ?? "—"),
            new("Status", Humanize(task.Status)),
            new("Priority", task.Priority.ToString(CultureInfo.InvariantCulture)),
            new("External key", Code(task.ExternalKey)),
            new("Updated", Stamp(task.UpdatedAt)),
        };

        var sections = new List<SlackModalBuilder.Section>
        {
            new("Description", task.Description ?? "_No description._"),
        };
        if (task.Paths is { Length: > 0 } paths)
            sections.Add(new("Paths", string.Join("\n", Array.ConvertAll(paths, p => $"• `{p}`"))));
        if (task.ReferencePointer is { Length: > 0 } rp)
            sections.Add(new("Reference", rp));
        if (boardUrl is not null)
            sections.Add(new(null, $":link: <{boardUrl}|Open the card on the GitHub board>"));

        return SlackModalBuilder.Build(
            "Task details", $":clipboard: {task.Title}", fields, sections,
            $"Created {Stamp(task.CreatedAt)}");
    }

    private static string BuildFindingModal(Finding finding, WorkTask? task, Plan? plan, Project? project, string? boardUrl)
    {
        var fields = new List<SlackBlockKitBuilder.Field>
        {
            new("Project", project?.DisplayName ?? "—"),
            new("Plan", plan?.Name ?? "—"),
            new("Audit task", task?.Title ?? "—"),
            new("Severity", Severity(finding.Severity)),
            new("Status", Humanize(finding.Status)),
            new("External key", Code(finding.ExternalKey)),
        };

        var sections = new List<SlackModalBuilder.Section>
        {
            new("Symptom", finding.Symptom ?? "—"),
            new("Invariant impact", finding.InvariantImpact ?? ""),
            new("Root cause", finding.RootCause ?? ""),
            new("Repro steps", finding.ReproSteps ?? ""),
            new("Adversarial input", finding.AdversarialInput ?? ""),
            new("Expected", finding.Expected ?? ""),
            new("Actual", finding.Actual ?? ""),
            new("Reference comparison", finding.ReferenceComparison ?? ""),
        };
        if (boardUrl is not null)
            sections.Add(new(null, $":link: <{boardUrl}|Open the audit task's card on the GitHub board>"));

        return SlackModalBuilder.Build(
            "Finding details", $"{SeverityEmoji(finding.Severity)} {finding.ExternalKey}", fields, sections,
            $"Created {Stamp(finding.CreatedAt)} · updated {Stamp(finding.UpdatedAt)}");
    }

    private static string BuildPlanModal(Plan plan, Project? project)
    {
        var fields = new List<SlackBlockKitBuilder.Field>
        {
            new("Project", project?.DisplayName ?? "—"),
            new("Type", Humanize(plan.PlanTypeId)),
            new("Status", Humanize(plan.Status)),
            new("Created", Stamp(plan.CreatedAt)),
        };
        var sections = new List<SlackModalBuilder.Section>
        {
            new("Objective", plan.Objective ?? "_No objective set._"),
        };
        var context = plan.ActivatedAt is { } a
            ? $"Activated {Stamp(a)}"
            : $"Created {Stamp(plan.CreatedAt)}";
        return SlackModalBuilder.Build("Plan details", $":clipboard: {plan.Name}", fields, sections, context);
    }

    // ----- formatting helpers -----

    private static string Humanize(string s)
    {
        var words = s.Replace('.', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..].ToLowerInvariant();
        return words.Length == 0 ? s : string.Join(' ', words);
    }

    private static string Code(string s) => string.IsNullOrEmpty(s) ? "—" : $"`{s}`";

    private static string Stamp(DateTimeOffset t)
        => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Severity(string? s)
        => string.IsNullOrWhiteSpace(s) ? "—" : $"{SeverityEmoji(s)} {Humanize(s)}";

    private static string SeverityEmoji(string? severity) => (severity ?? "").ToLowerInvariant() switch
    {
        "critical" => ":small_red_triangle:",
        "high"     => ":small_orange_diamond:",
        "medium"   => ":small_blue_diamond:",
        "low"      => ":white_small_square:",
        _          => ":grey_question:",
    };
}
