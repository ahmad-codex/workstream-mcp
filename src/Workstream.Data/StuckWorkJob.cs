using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Workstream.Data.StuckWork;

/// <summary>
/// Hourly background job (§11.3). Computes the stuck-work view and persists it to
/// <c>stuck_work_reports</c>. The report is also posted to a configured Slack channel
/// (the system Slack workspace's #workstream-ops), but the post is enqueued through the
/// normal Slack outbox so the job itself remains DB-only.
/// </summary>
public sealed class StuckWorkJob : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<StuckWorkJob> _log;

    public StuckWorkJob(IDbConnectionFactory factory, ILogger<StuckWorkJob> log)
    {
        _factory = factory; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once shortly after startup, then on the cadence.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "stuck-work job iteration failed");
            }
            try { await Task.Delay(Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // Inline a single query that bundles the four categories from §11.3.
        const string sql = """
            WITH stuck_tasks AS (
                SELECT count(*) AS n FROM tasks
                WHERE claim_token IS NOT NULL AND claimed_until < now() - interval '15 minutes'
            ),
            stuck_findings AS (
                SELECT count(*) AS n FROM findings
                WHERE status = 'pending_verification' AND created_at < now() - interval '24 hours'
            ),
            stuck_attempts AS (
                SELECT count(*) AS n FROM attempts
                WHERE claim_token IS NOT NULL AND claimed_until < now()
            ),
            silent_plans AS (
                SELECT count(*) AS n FROM plans p
                WHERE p.status = 'active'
                  AND NOT EXISTS (
                      SELECT 1 FROM events e
                      WHERE e.entity_type = 'task'
                        AND e.entity_id IN (SELECT id FROM tasks WHERE plan_id = p.id)
                        AND e.at > now() - interval '24 hours'
                  )
            )
            SELECT jsonb_build_object(
              'stuck_tasks',     (SELECT n FROM stuck_tasks),
              'stuck_findings',  (SELECT n FROM stuck_findings),
              'stuck_attempts',  (SELECT n FROM stuck_attempts),
              'silent_plans',    (SELECT n FROM silent_plans),
              'generated_at',    now()
            )::text;
            """;
        var summary = await conn.ExecuteScalarAsync<string>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO stuck_work_reports (summary) VALUES (@summary::jsonb)",
            new { summary }, cancellationToken: ct)).ConfigureAwait(false);
        _log.LogInformation("stuck-work report written: {Summary}", summary);
    }
}
