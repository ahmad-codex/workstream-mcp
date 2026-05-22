using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workstream.Data.Repositories;

namespace Workstream.Api.Dispatch;

/// <summary>
/// Background hosted service that drains <c>audit_dispatch_log</c>. For each queued
/// request it resolves the project and its repo, then hands off to an
/// <see cref="IAuditRunner"/> which launches the project's audit orchestrator. Same
/// outbox-worker shape as <c>SlackNotifyWorker</c> / <c>BoardSyncWorker</c>: the
/// request_audit MCP call only commits a Postgres row; the slow external launch happens
/// here, with retries.
///
/// Idle unless <see cref="AuditDispatchOptions.Enabled"/> — the feature ships dark.
/// </summary>
public sealed class AuditDispatchWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 4;
    private const int MaxAttempts = 3;

    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;
    private readonly IUserRepository _users;
    private readonly IAuditRunner _runner;
    private readonly AuditDispatchOptions _opts;
    private readonly ILogger<AuditDispatchWorker> _log;

    public AuditDispatchWorker(IOutboxRepository outbox, IProjectRepository projects,
        IUserRepository users, IAuditRunner runner, IOptions<AuditDispatchOptions> opts,
        ILogger<AuditDispatchWorker> log)
    {
        _outbox = outbox; _projects = projects; _users = users;
        _runner = runner; _opts = opts.Value; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("audit dispatch worker disabled (AuditDispatch:Enabled=false); not polling");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await _outbox.ClaimAuditDispatchBatchAsync(BatchSize, stoppingToken).ConfigureAwait(false);
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
                _log.LogWarning(ex, "audit dispatch worker loop failed; retrying");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(AuditDispatchRow row, CancellationToken ct)
    {
        try
        {
            var project = await _projects.GetAsync(row.ProjectId, ct).ConfigureAwait(false);
            if (project is null)
            {
                await _outbox.MarkAuditDispatchResultAsync(row.Id, "failed", null, "project not found", null, ct).ConfigureAwait(false);
                return;
            }

            // A 'cancel' row only needs the slug — the runner signals the live session.
            if (string.Equals(row.Action, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                var cancel = await _runner.RunAsync(
                    new AuditRunContext(project.Id, project.Slug, null, null, null,
                        Array.Empty<RepoRef>(), "cancel"), ct).ConfigureAwait(false);
                if (cancel.Ok)
                {
                    _log.LogInformation("audit cancel signalled for {Slug}", project.Slug);
                    await _outbox.MarkAuditDispatchResultAsync(row.Id, "success", cancel.Summary, null, null, ct).ConfigureAwait(false);
                }
                else
                {
                    await FailOrRetryAsync(row, cancel.Error ?? "cancel failed", ct).ConfigureAwait(false);
                }
                return;
            }

            // Prefer a working (non reference-only) repo as the primary; the rest are
            // reference checkouts the orchestrator wants alongside.
            var repos = await _projects.ListReposAsync(row.ProjectId, ct).ConfigureAwait(false);
            var primary = repos.FirstOrDefault(r => !r.IsReferenceOnly) ?? repos.FirstOrDefault();
            var referenceRepos = repos.Where(r => r.IsReferenceOnly)
                .Select(r => new RepoRef(r.GithubOwner, r.GithubRepo))
                .ToList();

            // The audit runs on the triggering user's behalf — resolve their MCP token so
            // the orchestrator's Workstream calls are attributed to them.
            var mcpToken = row.RequestedBy is { } uid
                ? await _users.GetMcpTokenAsync(uid, ct).ConfigureAwait(false)
                : null;

            var result = await _runner.RunAsync(
                new AuditRunContext(project.Id, project.Slug, primary?.GithubOwner, primary?.GithubRepo,
                    mcpToken, referenceRepos, "run"), ct)
                .ConfigureAwait(false);

            if (result.Ok)
            {
                _log.LogInformation("audit dispatched for {Slug}", project.Slug);
                await _outbox.MarkAuditDispatchResultAsync(row.Id, "success", result.Summary, null, null, ct).ConfigureAwait(false);
            }
            else
            {
                await FailOrRetryAsync(row, result.Error ?? "dispatch failed", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await FailOrRetryAsync(row, ex.Message, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Retry with exponential backoff until the attempt cap, then mark failed.</summary>
    private async Task FailOrRetryAsync(AuditDispatchRow row, string error, CancellationToken ct)
    {
        if (row.Attempts >= MaxAttempts)
        {
            _log.LogWarning("audit dispatch row {Id} failed at max attempts: {Error}", row.Id, error);
            await _outbox.MarkAuditDispatchResultAsync(row.Id, "failed", null, error, null, ct).ConfigureAwait(false);
        }
        else
        {
            var delay = TimeSpan.FromMinutes(Math.Pow(2, row.Attempts));
            await _outbox.MarkAuditDispatchResultAsync(row.Id, "retry", null, error, delay, ct).ConfigureAwait(false);
        }
    }
}
