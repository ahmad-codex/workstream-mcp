using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workstream.Data.Repositories;

namespace Workstream.Slack;

/// <summary>
/// Background hosted service that drains <c>slack_notify_log</c> (§8.3). One per API replica.
/// On success, persists <c>slack_ts</c> back to the row so subsequent notifications on the
/// same entity reply in the same Slack thread (§8.4).
/// </summary>
public sealed class SlackNotifyWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 16;
    private const int MaxAttempts = 5;

    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;
    private readonly SlackClient _slack;
    private readonly ISlackBotTokenResolver _tokens;
    private readonly ILogger<SlackNotifyWorker> _log;

    public SlackNotifyWorker(IOutboxRepository outbox, IProjectRepository projects,
        SlackClient slack, ISlackBotTokenResolver tokens, ILogger<SlackNotifyWorker> log)
    {
        _outbox = outbox; _projects = projects; _slack = slack; _tokens = tokens; _log = log;
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
            var token = await _tokens.ResolveAsync(slackCfg.BotTokenSecretRef, ct).ConfigureAwait(false);
            var result = await _slack.PostMessageAsync(token, row.ChannelId, row.Body, row.ThreadTs, ct).ConfigureAwait(false);
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
}
