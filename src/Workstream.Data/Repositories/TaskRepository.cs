using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

/// <summary>
/// Postgres-backed task repository. Implements the claim primitive described in §6.
/// Every mutation runs in a single transaction so the event row is always emitted in lockstep
/// with the business row.
/// </summary>
public sealed class TaskRepository : ITaskRepository
{
    private readonly IDbConnectionFactory _factory;

    public TaskRepository(IDbConnectionFactory factory) => _factory = factory;

    // ----- SELECT shape -----
    // Postgres returns snake_case; we alias to PascalCase so Dapper can hydrate the record
    // and the nested ClaimState struct without registering a type map.
    private const string TaskColumns = """
        id                   AS "Id",
        plan_id              AS "PlanId",
        phase_id             AS "PhaseId",
        external_key         AS "ExternalKey",
        title                AS "Title",
        description          AS "Description",
        paths                AS "Paths",
        reference_pointer    AS "ReferencePointer",
        priority             AS "Priority",
        status               AS "Status",
        assignee_actor_id    AS "AssigneeActorId",
        github_board_item_id AS "GithubBoardItemId",
        github_issue_number  AS "GithubIssueNumber",
        github_issue_node_id AS "GithubIssueNodeId",
        claim_actor_id       AS "ClaimActorId",
        claim_role           AS "ClaimRole",
        claim_token          AS "ClaimToken",
        claimed_at           AS "ClaimedAt",
        claimed_until        AS "ClaimedUntil",
        created_at           AS "CreatedAt",
        updated_at           AS "UpdatedAt"
        """;

    public async Task<WorkTask?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await ReadOneAsync(conn, "WHERE id = @id LIMIT 1", new { id }, ct).ConfigureAwait(false);
    }

    public async Task<WorkTask?> GetByClaimTokenAsync(Guid claimToken, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await ReadOneAsync(conn, "WHERE claim_token = @claimToken LIMIT 1", new { claimToken }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkTask>> ListByPlanAsync(Guid planId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await ReadManyAsync(conn,
            "WHERE plan_id = @planId ORDER BY priority DESC, created_at ASC",
            new { planId }, ct).ConfigureAwait(false);
    }

    public async Task<WorkTask> InsertAsync(WorkTaskInsert input, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string insertSql = $"""
            INSERT INTO tasks (
                plan_id, phase_id, external_key, title, description,
                paths, reference_pointer, priority, status
            )
            VALUES (
                @PlanId, @PhaseId, @ExternalKey, @Title, @Description,
                @Paths, @ReferencePointer, @Priority, 'pending'
            )
            RETURNING {TaskColumns}
            """;
        var row = await conn.QuerySingleAsync<TaskRow>(new CommandDefinition(insertSql, input, tx, cancellationToken: ct)).ConfigureAwait(false);

        if (input.DependsOn is { Length: > 0 } deps)
        {
            var distinctDeps = deps.Where(d => d != row.Id).Distinct().ToArray();
            if (distinctDeps.Length > 0)
            {
                // Validate all predecessors exist on the same plan.
                var sameplan = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM tasks WHERE id = ANY(@ids) AND plan_id = @planId
                    """, new { ids = distinctDeps, planId = row.PlanId }, tx, cancellationToken: ct)).ConfigureAwait(false);
                if (sameplan != distinctDeps.Length)
                    throw new InvalidOperationException("one or more depends_on tasks do not exist on this plan");

                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO task_dependencies (task_id, depends_on_task_id)
                    SELECT @taskId, dep_id
                    FROM unnest(@deps::uuid[]) AS dep_id
                    ON CONFLICT DO NOTHING
                    """, new { taskId = row.Id, deps = distinctDeps }, tx, cancellationToken: ct)).ConfigureAwait(false);

                // Cycle check: walk every edge starting from this task; if we reach
                // this task again, there's a cycle.
                var cycle = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                    WITH RECURSIVE walk(node, depth) AS (
                        SELECT depends_on_task_id, 1 FROM task_dependencies WHERE task_id = @taskId
                        UNION
                        SELECT td.depends_on_task_id, w.depth + 1
                        FROM task_dependencies td
                        JOIN walk w ON w.node = td.task_id
                        WHERE w.depth < 100
                    )
                    SELECT COUNT(*) FROM walk WHERE node = @taskId
                    """, new { taskId = row.Id }, tx, cancellationToken: ct)).ConfigureAwait(false);
                if (cycle > 0)
                    throw new InvalidOperationException("dependency cycle detected");
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row.ToDomain();
    }

    public async Task<IReadOnlyList<Guid>> GetDependenciesAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT depends_on_task_id FROM task_dependencies WHERE task_id = @taskId ORDER BY created_at",
            new { taskId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetDependenciesForPlanAsync(Guid planId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(Guid TaskId, Guid DependsOn)>(new CommandDefinition("""
            SELECT td.task_id AS TaskId, td.depends_on_task_id AS DependsOn
            FROM task_dependencies td
            JOIN tasks t ON t.id = td.task_id
            WHERE t.plan_id = @planId
            ORDER BY td.task_id, td.created_at
            """, new { planId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows
            .GroupBy(r => r.TaskId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.DependsOn).ToList());
    }

    public async Task<IReadOnlyList<Guid>> GetUnmetDependenciesAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // Terminal-for-deps: a predecessor in any of these states unblocks its dependents.
        // 'needs_human_review' is intentionally NOT in this set — that state means "a human
        // still needs to act," so it shouldn't auto-unblock downstream work.
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT td.depends_on_task_id
            FROM task_dependencies td
            JOIN tasks dep ON dep.id = td.depends_on_task_id
            WHERE td.task_id = @taskId
              AND dep.status NOT IN ('done','deferred','skipped','out_of_scope')
            """, new { taskId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task WireDependenciesAsync(Guid taskId, Guid planId, IReadOnlyList<Guid> dependsOn, CancellationToken ct = default)
    {
        if (dependsOn.Count == 0) return;
        var deps = dependsOn.Where(d => d != taskId).Distinct().ToArray();
        if (deps.Length == 0) return;

        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var sameplan = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM tasks WHERE id = ANY(@ids) AND plan_id = @planId
            """, new { ids = deps, planId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (sameplan != deps.Length)
            throw new InvalidOperationException("one or more depends_on tasks do not exist on this plan");

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO task_dependencies (task_id, depends_on_task_id)
            SELECT @taskId, dep_id FROM unnest(@deps::uuid[]) AS dep_id
            ON CONFLICT DO NOTHING
            """, new { taskId, deps }, tx, cancellationToken: ct)).ConfigureAwait(false);

        var cycle = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            WITH RECURSIVE walk(node, depth) AS (
                SELECT depends_on_task_id, 1 FROM task_dependencies WHERE task_id = @taskId
                UNION
                SELECT td.depends_on_task_id, w.depth + 1
                FROM task_dependencies td
                JOIN walk w ON w.node = td.task_id
                WHERE w.depth < 100
            )
            SELECT COUNT(*) FROM walk WHERE node = @taskId
            """, new { taskId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (cycle > 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException("dependency cycle detected");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Claim primitive (§6.1)
    //
    // Implemented as a CTE + UPDATE ... RETURNING. FOR UPDATE SKIP LOCKED means concurrent
    // claimers never block: each transaction sees a different row or zero rows.
    //
    // Claimable rows are either:
    //   (a) status = 'pending' AND claim columns NULL  (normal case), OR
    //   (b) status = 'claimed' AND claim_token NOT NULL AND claimed_until < now()  (reclaim
    //       after an agent died mid-claim). On reclaim we keep status = 'claimed' and
    //       overwrite the claim columns with the new actor's data.
    //
    // The hourly sweeper (SweepExpiredClaimsAsync) handles longer-lived stuck claims; it
    // resets stale 'claimed' rows back to 'pending' so the normal claim path can pick them up.
    // -----------------------------------------------------------------------

    public async Task<ClaimedTask?> ClaimNextAsync(
        Guid planId, Guid actorId, string role, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            WITH next AS (
                SELECT id AS picked_id
                FROM tasks t
                WHERE plan_id = @planId
                  AND (
                      (status = 'pending' AND claim_token IS NULL)
                      OR (status = 'claimed' AND claim_token IS NOT NULL AND claimed_until < now())
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM task_dependencies td
                      JOIN tasks dep ON dep.id = td.depends_on_task_id
                      WHERE td.task_id = t.id
                        AND dep.status NOT IN ('done','deferred','skipped','out_of_scope')
                  )
                ORDER BY priority DESC, created_at ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE tasks t
            SET claim_actor_id = @actorId,
                claim_role     = @role,
                claim_token    = gen_random_uuid(),
                claimed_at     = now(),
                claimed_until  = now() + (@ttlSeconds || ' seconds')::interval,
                status         = 'claimed'
            FROM next
            WHERE t.id = next.picked_id
            RETURNING {TaskColumns};
            """;
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(sql,
            new { planId, actorId, role, ttlSeconds = (int)ttl.TotalSeconds }, cancellationToken: ct))
            .ConfigureAwait(false);
        if (row is null) return null;
        await EmitClaimedEventAsync(conn, row.Id, actorId, role, ct).ConfigureAwait(false);
        var task = row.ToDomain();
        return new ClaimedTask(task, task.Claim.Token!.Value);
    }

    public async Task<ClaimedTask?> ClaimSpecificAsync(
        Guid taskId, Guid actorId, string role, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            WITH target AS (
                SELECT id AS picked_id
                FROM tasks t
                WHERE id = @taskId
                  AND (
                      (status = 'pending' AND claim_token IS NULL)
                      OR (status = 'claimed' AND claim_token IS NOT NULL AND claimed_until < now())
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM task_dependencies td
                      JOIN tasks dep ON dep.id = td.depends_on_task_id
                      WHERE td.task_id = t.id
                        AND dep.status NOT IN ('done','deferred','skipped','out_of_scope')
                  )
                FOR UPDATE
            )
            UPDATE tasks t
            SET claim_actor_id = @actorId,
                claim_role     = @role,
                claim_token    = gen_random_uuid(),
                claimed_at     = now(),
                claimed_until  = now() + (@ttlSeconds || ' seconds')::interval,
                status         = 'claimed'
            FROM target
            WHERE t.id = target.picked_id
            RETURNING {TaskColumns};
            """;
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(sql,
            new { taskId, actorId, role, ttlSeconds = (int)ttl.TotalSeconds }, cancellationToken: ct))
            .ConfigureAwait(false);
        if (row is null) return null;
        await EmitClaimedEventAsync(conn, row.Id, actorId, role, ct).ConfigureAwait(false);
        var task = row.ToDomain();
        return new ClaimedTask(task, task.Claim.Token!.Value);
    }

    public async Task<WorkTask?> ReleaseClaimAsync(
        Guid claimToken, Guid actorId, string? reason, bool resetStatusToPending, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            UPDATE tasks
            SET claim_actor_id = NULL,
                claim_role     = NULL,
                claim_token    = NULL,
                claimed_at     = NULL,
                claimed_until  = NULL,
                status         = CASE WHEN @reset THEN 'pending' ELSE status END
            WHERE claim_token = @claimToken
            RETURNING {TaskColumns};
            """;
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(sql,
            new { claimToken, reset = resetStatusToPending }, cancellationToken: ct))
            .ConfigureAwait(false);
        if (row is null) return null;
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, payload)
            VALUES (@actorId, 'task', @taskId, 'released', jsonb_build_object('reason', @reason))
            """, new { actorId, taskId = row.Id, reason }, cancellationToken: ct)).ConfigureAwait(false);
        return row.ToDomain();
    }

    public async Task<WorkTask?> RefreshClaimAsync(
        Guid claimToken, Guid actorId, TimeSpan extendBy, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Match the claim token, the holding actor (so a different actor can't extend
        // someone else's claim), and require the existing claim to still be live
        // (claimed_until > now()). An already-expired claim is NOT eligible — it must
        // go through release_claim and re-claim, otherwise an agent that lost the race
        // to the sweeper could revive a row that another claimer has since taken.
        var sql = $"""
            UPDATE tasks
            SET claimed_until = now() + (@extendSeconds || ' seconds')::interval
            WHERE claim_token   = @claimToken
              AND claim_actor_id = @actorId
              AND claimed_until > now()
            RETURNING {TaskColumns};
            """;
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(sql,
            new { claimToken, actorId, extendSeconds = (int)extendBy.TotalSeconds }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        if (row is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, payload)
            VALUES (@actorId, 'task', @taskId, 'claim_refreshed',
                    jsonb_build_object('extend_seconds', @extendSeconds, 'claimed_until', @until))
            """, new { actorId, taskId = row.Id, extendSeconds = (int)extendBy.TotalSeconds, until = row.ClaimedUntil }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row.ToDomain();
    }

    public async Task<WorkTask?> UpdateStatusWithClaimAsync(
        Guid claimToken, Guid actorId, string newStatus, bool clearClaim,
        string eventType, string? eventPayloadJson, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var lockSql = $"""
            SELECT {TaskColumns}
            FROM tasks
            WHERE claim_token = @claimToken
            FOR UPDATE
            """;
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(lockSql,
            new { claimToken }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        if (row is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }
        if (row.ClaimedUntil < DateTimeOffset.UtcNow)
        {
            // Claim expired between request and validation. Treat as stale.
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        var updateSql = clearClaim
            ? $"""
              UPDATE tasks
              SET status = @newStatus,
                  claim_actor_id = NULL,
                  claim_role = NULL,
                  claim_token = NULL,
                  claimed_at = NULL,
                  claimed_until = NULL
              WHERE id = @id
              RETURNING {TaskColumns}
              """
            : $"""
              UPDATE tasks SET status = @newStatus WHERE id = @id RETURNING {TaskColumns}
              """;
        var updated = await conn.QuerySingleAsync<TaskRow>(new CommandDefinition(updateSql,
            new { newStatus, id = row.Id }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, from_state, to_state, payload)
            VALUES (@actorId, 'task', @id, @eventType, @fromState, @toState, COALESCE(@payload::jsonb, '{}'::jsonb))
            """,
            new { actorId, id = row.Id, eventType, fromState = row.Status, toState = newStatus, payload = eventPayloadJson },
            tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return updated.ToDomain();
    }

    public async Task<WorkTask?> OverrideStatusAsync(
        Guid taskId, Guid actorId, string newStatus, string reason, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var lockSql = $"SELECT {TaskColumns} FROM tasks WHERE id = @taskId FOR UPDATE";
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(lockSql,
            new { taskId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        var updateSql = $"""
            UPDATE tasks
            SET status = @newStatus,
                claim_actor_id = NULL, claim_role = NULL, claim_token = NULL,
                claimed_at = NULL, claimed_until = NULL
            WHERE id = @taskId
            RETURNING {TaskColumns}
            """;
        var updated = await conn.QuerySingleAsync<TaskRow>(new CommandDefinition(updateSql,
            new { newStatus, taskId }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, from_state, to_state, payload)
            VALUES (@actorId, 'task', @taskId, 'override', @fromState, @toState, jsonb_build_object('reason', @reason))
            """,
            new { actorId, taskId, fromState = row.Status, toState = newStatus, reason },
            tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return updated.ToDomain();
    }

    public async Task SetGithubBoardItemIdAsync(Guid taskId, string boardItemId, long? boardItemNumber, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tasks SET github_board_item_id = @boardItemId, github_board_item_number = COALESCE(@boardItemNumber, github_board_item_number) WHERE id = @taskId",
            new { taskId, boardItemId, boardItemNumber }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<long?> GetGithubBoardItemNumberAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT github_board_item_number FROM tasks WHERE id = @taskId",
            new { taskId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> SweepExpiredClaimsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // Reset 'claimed' rows whose claim expired back to 'pending' so they re-enter the queue.
        // In-progress rows with expired claims are surfaced in stuck-work reports and require
        // human or override action — we don't auto-regress them because the orchestrator may
        // still recover.
        var affected = await conn.ExecuteAsync(new CommandDefinition("""
            WITH stale AS (
                SELECT id FROM tasks
                WHERE status = 'claimed' AND claim_token IS NOT NULL AND claimed_until < now()
                FOR UPDATE SKIP LOCKED
            )
            UPDATE tasks t
            SET status = 'pending',
                claim_actor_id = NULL,
                claim_role     = NULL,
                claim_token    = NULL,
                claimed_at     = NULL,
                claimed_until  = NULL
            FROM stale
            WHERE t.id = stale.id;
            """, cancellationToken: ct)).ConfigureAwait(false);
        return affected;
    }

    // ----- helpers -----

    private static async Task EmitClaimedEventAsync(NpgsqlConnection conn, Guid taskId, Guid actorId, string role, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, to_state, payload)
            VALUES (@actorId, 'task', @taskId, 'claimed', 'claimed', jsonb_build_object('role', @role))
            """, new { actorId, taskId, role }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task<WorkTask?> ReadOneAsync(NpgsqlConnection conn, string whereClause, object? parameters, CancellationToken ct)
    {
        var sql = $"SELECT {TaskColumns} FROM tasks {whereClause}";
        var row = await conn.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))
            .ConfigureAwait(false);
        return row?.ToDomain();
    }

    private static async Task<IReadOnlyList<WorkTask>> ReadManyAsync(NpgsqlConnection conn, string whereClause, object? parameters, CancellationToken ct)
    {
        var sql = $"SELECT {TaskColumns} FROM tasks {whereClause}";
        var rows = await conn.QueryAsync<TaskRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    // ----- flat row shape -----
    // Property-init (not positional) so Dapper uses parameterless-ctor + property setters.
    // The positional path requires constructor-signature-exact column types, which Npgsql
    // doesn't surface for nullable references or for timestamptz (returns DateTime, not
    // DateTimeOffset). The projection to WorkTask below stays positional because WorkTask
    // is never hydrated by Dapper.
    internal sealed record TaskRow
    {
        public Guid     Id                  { get; init; }
        public Guid     PlanId              { get; init; }
        public Guid?    PhaseId             { get; init; }
        public string   ExternalKey         { get; init; } = "";
        public string   Title               { get; init; } = "";
        public string?  Description         { get; init; }
        public string[]? Paths              { get; init; }
        public string?  ReferencePointer    { get; init; }
        public int      Priority            { get; init; }
        public string   Status              { get; init; } = "pending";
        public Guid?    AssigneeActorId     { get; init; }
        public string?  GithubBoardItemId   { get; init; }
        public int?     GithubIssueNumber   { get; init; }
        public string?  GithubIssueNodeId   { get; init; }
        public Guid?    ClaimActorId        { get; init; }
        public string?  ClaimRole           { get; init; }
        public Guid?    ClaimToken          { get; init; }
        public DateTimeOffset? ClaimedAt    { get; init; }
        public DateTimeOffset? ClaimedUntil { get; init; }
        public DateTimeOffset  CreatedAt    { get; init; }
        public DateTimeOffset  UpdatedAt    { get; init; }

        public WorkTask ToDomain() => new(
            Id, PlanId, PhaseId, ExternalKey, Title, Description, Paths, ReferencePointer,
            Priority, Status, AssigneeActorId, GithubBoardItemId, GithubIssueNumber, GithubIssueNodeId,
            new ClaimState(ClaimActorId, ClaimRole, ClaimToken, ClaimedAt, ClaimedUntil),
            CreatedAt, UpdatedAt);
    }
}
