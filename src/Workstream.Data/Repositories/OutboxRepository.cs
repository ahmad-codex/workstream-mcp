using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Workstream.Data.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly IDbConnectionFactory _factory;
    public OutboxRepository(IDbConnectionFactory factory) => _factory = factory;

    // ------- Board sync outbox -------

    public async Task<long> EnqueueBoardSyncAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus, string? assigneeGithubUsername = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO board_sync_log (task_id, board_id, target_column, target_status, assignee_github_username)
            VALUES (@taskId, @boardId, @targetColumn, @targetStatus, @assigneeGithubUsername)
            RETURNING id
            """, new { taskId, boardId, targetColumn, targetStatus, assigneeGithubUsername }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BoardSyncRow>> ClaimBoardSyncBatchAsync(int batchSize, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // The worker can run as multiple instances; pg_advisory_xact_lock would coordinate, but
        // FOR UPDATE SKIP LOCKED on the outbox rows is the simpler answer.
        var sql = """
            WITH next AS (
                SELECT id
                FROM board_sync_log
                WHERE result IN ('pending','retry') AND next_attempt_at <= now()
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT @batchSize
            )
            UPDATE board_sync_log b
            SET attempts = attempts + 1,
                last_attempted_at = now(),
                result = 'pending'
            FROM next
            WHERE b.id = next.id
            RETURNING b.id            AS Id,
                      b.task_id       AS TaskId,
                      b.board_id      AS BoardId,
                      b.target_column AS TargetColumn,
                      b.target_status AS TargetStatus,
                      b.sync_marker   AS SyncMarker,
                      b.attempts      AS Attempts,
                      b.next_attempt_at AS NextAttemptAt,
                      b.result        AS Result,
                      b.assignee_github_username AS AssigneeGithubUsername
            """;
        var rows = await conn.QueryAsync<BoardSyncRow>(new CommandDefinition(sql, new { batchSize }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task MarkBoardSyncResultAsync(long id, string result, string? error, string? githubResponseId, TimeSpan? retryDelay, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE board_sync_log
            SET result = @result,
                error = @error,
                github_response_id = @githubResponseId,
                next_attempt_at = CASE WHEN @retrySeconds IS NULL THEN next_attempt_at
                                       ELSE now() + (@retrySeconds || ' seconds')::interval END
            WHERE id = @id
            """, new { id, result, error, githubResponseId, retrySeconds = retryDelay is { } d ? (int?)d.TotalSeconds : null }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<bool> RecentSyncMarkerExistsAsync(Guid boardItemTaskId, TimeSpan within, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM board_sync_log
                WHERE task_id = @taskId AND result = 'success'
                  AND last_attempted_at > now() - (@seconds || ' seconds')::interval
            )
            """, new { taskId = boardItemTaskId, seconds = (int)within.TotalSeconds }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    // ------- Slack outbox -------

    public async Task<long> EnqueueSlackAsync(SlackNotifyRow row, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO slack_notify_log (
                event_id, project_id, plan_id, entity_type, entity_id, channel_id, notification_type, body, blocks_json, color,
                author_name, author_icon, author_link, thread_ts
            )
            VALUES (@EventId, @ProjectId, @PlanId, @EntityType, @EntityId, @ChannelId, @NotificationType, @Body, @BlocksJson, @Color,
                @AuthorName, @AuthorIcon, @AuthorLink, @ThreadTs)
            RETURNING id
            """, row, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SlackNotifyRow>> ClaimSlackBatchAsync(int batchSize, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = """
            WITH next AS (
                SELECT id FROM slack_notify_log
                WHERE result IN ('pending','retry') AND next_attempt_at <= now()
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT @batchSize
            )
            UPDATE slack_notify_log s
            SET attempts = attempts + 1, last_attempted_at = now(), result = 'pending'
            FROM next
            WHERE s.id = next.id
            RETURNING s.id              AS Id,
                      s.event_id        AS EventId,
                      s.project_id      AS ProjectId,
                      s.plan_id         AS PlanId,
                      s.entity_type     AS EntityType,
                      s.entity_id       AS EntityId,
                      s.channel_id      AS ChannelId,
                      s.notification_type AS NotificationType,
                      s.body            AS Body,
                      s.blocks_json     AS BlocksJson,
                      s.color           AS Color,
                      s.author_name     AS AuthorName,
                      s.author_icon     AS AuthorIcon,
                      s.author_link     AS AuthorLink,
                      s.thread_ts       AS ThreadTs,
                      s.slack_ts        AS SlackTs,
                      s.attempts        AS Attempts,
                      s.next_attempt_at AS NextAttemptAt,
                      s.result          AS Result
            """;
        var rows = await conn.QueryAsync<SlackNotifyRow>(new CommandDefinition(sql, new { batchSize }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task MarkSlackResultAsync(long id, string result, string? error, string? slackTs, TimeSpan? retryDelay, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE slack_notify_log
            SET result = @result, error = @error, slack_ts = COALESCE(@slackTs, slack_ts),
                next_attempt_at = CASE WHEN @retrySeconds IS NULL THEN next_attempt_at
                                       ELSE now() + (@retrySeconds || ' seconds')::interval END
            WHERE id = @id
            """, new { id, result, error, slackTs, retrySeconds = retryDelay is { } d ? (int?)d.TotalSeconds : null }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<string?> GetParentSlackTsAsync(string entityType, Guid entityId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT slack_ts FROM slack_notify_log
            WHERE entity_type = @entityType AND entity_id = @entityId
              AND slack_ts IS NOT NULL AND thread_ts IS NULL
            ORDER BY created_at ASC
            LIMIT 1
            """, new { entityType, entityId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    // ------- Audit dispatch outbox -------

    public async Task<long> EnqueueAuditDispatchAsync(Guid projectId, Guid? requestedBy, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO audit_dispatch_log (project_id, requested_by)
            VALUES (@projectId, @requestedBy)
            RETURNING id
            """, new { projectId, requestedBy }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditDispatchRow>> ClaimAuditDispatchBatchAsync(int batchSize, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = """
            WITH next AS (
                SELECT id FROM audit_dispatch_log
                WHERE status IN ('pending','retry') AND next_attempt_at <= now()
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT @batchSize
            )
            UPDATE audit_dispatch_log a
            SET attempts = attempts + 1, last_attempted_at = now(), status = 'running'
            FROM next
            WHERE a.id = next.id
            RETURNING a.id              AS Id,
                      a.project_id      AS ProjectId,
                      a.requested_by    AS RequestedBy,
                      a.attempts        AS Attempts,
                      a.next_attempt_at AS NextAttemptAt,
                      a.status          AS Status
            """;
        var rows = await conn.QueryAsync<AuditDispatchRow>(new CommandDefinition(sql, new { batchSize }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task MarkAuditDispatchResultAsync(long id, string status, string? result, string? error, TimeSpan? retryDelay, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE audit_dispatch_log
            SET status = @status, result = @result, error = @error,
                next_attempt_at = CASE WHEN @retrySeconds IS NULL THEN next_attempt_at
                                       ELSE now() + (@retrySeconds || ' seconds')::interval END
            WHERE id = @id
            """, new { id, status, result, error, retrySeconds = retryDelay is { } d ? (int?)d.TotalSeconds : null }, cancellationToken: ct))
            .ConfigureAwait(false);
    }
}
