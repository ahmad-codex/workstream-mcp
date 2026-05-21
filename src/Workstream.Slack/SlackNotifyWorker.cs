using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;

namespace Workstream.Slack;

/// <summary>
/// Background hosted service that drains <c>slack_notify_log</c> (§8.3). One per API replica.
/// On success, persists <c>slack_ts</c> back to the row.
///
/// Re-link enrichment: when an MCP request enqueues a task notification, it composes the
/// body before the board-sync worker has had a chance to create the GitHub draft item
/// (lazy create lives in <c>BoardSyncWorker</c>). The first body therefore contains the
/// board-page URL (not the per-item pane URL). Before posting, we re-read the task and
/// rewrite any <c>&lt;boardUrl|title&gt;</c> link in the body to the pane URL if a
/// <c>github_board_item_number</c> is now available. This keeps the first-post race-free
/// without putting GitHub API calls inside the request transaction.
/// </summary>
public sealed class SlackNotifyWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 16;
    private const int MaxAttempts = 5;

    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;
    private readonly IPlanRepository _plans;
    private readonly ITaskRepository _tasks;
    private readonly SlackClient _slack;
    private readonly ISlackBotTokenResolver _tokens;
    private readonly ILogger<SlackNotifyWorker> _log;

    public SlackNotifyWorker(IOutboxRepository outbox, IProjectRepository projects,
        IPlanRepository plans, ITaskRepository tasks,
        SlackClient slack, ISlackBotTokenResolver tokens, ILogger<SlackNotifyWorker> log)
    {
        _outbox = outbox; _projects = projects; _plans = plans; _tasks = tasks;
        _slack = slack; _tokens = tokens; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await _outbox.ClaimSlackBatchAsync(BatchSize, stoppingToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                foreach (var row in batch)
                    await ProcessAsync(row, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "slack notify worker loop failed; retrying");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(SlackNotifyRow row, CancellationToken ct)
    {
        try
        {
            if (row.ProjectId is null)
            {
                await _outbox.MarkSlackResultAsync(row.Id, "failed", "row missing project_id", null, null, ct).ConfigureAwait(false);
                return;
            }
            var slackCfg = await _projects.GetSlackAsync(row.ProjectId.Value, ct).ConfigureAwait(false);
            if (slackCfg is null)
            {
                await _outbox.MarkSlackResultAsync(row.Id, "failed", "no slack config for project", null, null, ct).ConfigureAwait(false);
                return;
            }

            // If this is a task notification and the plan is bound to a board but the
            // task doesn't have its github_board_item_number persisted yet, the board
            // sync worker hasn't finished creating the draft item. Defer this post
            // briefly so the next dequeue can build the deep-link URL. Cap at a few
            // retries so a perma-failing board sync doesn't block the slack post.
            if (await ShouldWaitForBoardItemAsync(row, ct).ConfigureAwait(false))
            {
                await _outbox.MarkSlackResultAsync(row.Id, "retry", null, null, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                return;
            }

            var (body, blocks) = await UpgradeBoardLinkToItemLinkAsync(row, ct).ConfigureAwait(false);

            var token = await _tokens.ResolveAsync(slackCfg.BotTokenSecretRef, ct).ConfigureAwait(false);
            var result = await _slack.PostMessageAsync(token, row.ChannelId, body, row.ThreadTs, row.Color,
                row.AuthorName, row.AuthorIcon, row.AuthorLink, blocks, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                throw new InvalidOperationException($"slack rejected post: {result.Error}");
            }
            await _outbox.MarkSlackResultAsync(row.Id, "success", null, result.Ts, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var nextAttempt = row.Attempts + 1;
            if (nextAttempt >= MaxAttempts)
            {
                _log.LogWarning(ex, "slack notify row {Id} failed at max attempts", row.Id);
                await _outbox.MarkSlackResultAsync(row.Id, "failed", ex.Message, null, null, ct).ConfigureAwait(false);
            }
            else
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, nextAttempt));
                await _outbox.MarkSlackResultAsync(row.Id, "retry", ex.Message, null, delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// True when the row should be deferred because the board worker hasn't yet
    /// persisted <c>github_board_item_number</c> for this task. After a handful of
    /// retries we give up and post the body as-is (board-page URL instead of pane URL).
    /// </summary>
    private async Task<bool> ShouldWaitForBoardItemAsync(SlackNotifyRow row, CancellationToken ct)
    {
        if (row.EntityType != EntityType.Task) return false;
        if (row.PlanId is null) return false;
        if (row.Attempts >= 4) return false;   // ~9-12s total wait, then give up

        var plan = await _plans.GetAsync(row.PlanId.Value, ct).ConfigureAwait(false);
        if (plan?.PrimaryBoardId is null) return false;

        var num = await _tasks.GetGithubBoardItemNumberAsync(row.EntityId, ct).ConfigureAwait(false);
        return num is null;
    }

    /// <summary>
    /// If the body or blocks carry a board-page link for a task whose item id is now
    /// known, rewrite it to the project's side-pane URL. Returns the (body, blocks) pair
    /// unchanged when there's no upgrade to do.
    /// </summary>
    private async Task<(string Body, string? Blocks)> UpgradeBoardLinkToItemLinkAsync(SlackNotifyRow row, CancellationToken ct)
    {
        if (row.EntityType != EntityType.Task) return (row.Body, row.BlocksJson);
        if (row.PlanId is null) return (row.Body, row.BlocksJson);

        var plan = await _plans.GetAsync(row.PlanId.Value, ct).ConfigureAwait(false);
        if (plan?.PrimaryBoardId is not { } boardId) return (row.Body, row.BlocksJson);

        var board = await _projects.GetBoardAsync(boardId, ct).ConfigureAwait(false);
        if (board is null) return (row.Body, row.BlocksJson);

        var num = await _tasks.GetGithubBoardItemNumberAsync(row.EntityId, ct).ConfigureAwait(false);
        if (num is null) return (row.Body, row.BlocksJson);

        var boardUrl = $"https://github.com/orgs/{board.GithubOwner}/projects/{board.GithubProjectNumber}";
        var paneUrl = $"{boardUrl}/views/1?pane=issue&itemId={num}";

        // Body: rewrite the mrkdwn link form <boardUrl|label>.
        var linkPattern = $"<{Regex.Escape(boardUrl)}\\|([^>]+)>";
        var body = Regex.Replace(row.Body, linkPattern, m => $"<{paneUrl}|{m.Groups[1].Value}>");

        // Blocks: the board URL appears bare (button `url`, section links). Upgrade only
        // the bare board-page form — a negative lookahead skips a URL already carrying
        // the /views… pane suffix so a second pass can't corrupt it.
        string? blocks = row.BlocksJson;
        if (blocks is not null)
            blocks = Regex.Replace(blocks, $"{Regex.Escape(boardUrl)}(?!/views)", paneUrl);

        return (body, blocks);
    }
}
