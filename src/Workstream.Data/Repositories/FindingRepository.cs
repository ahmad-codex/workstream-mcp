using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public sealed class FindingRepository : IFindingRepository
{
    private readonly IDbConnectionFactory _factory;

    public FindingRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string Columns = """
        id                   AS "Id",
        task_id              AS "TaskId",
        external_key         AS "ExternalKey",
        severity             AS "Severity",
        invariant_impact     AS "InvariantImpact",
        symptom              AS "Symptom",
        root_cause           AS "RootCause",
        repro_steps          AS "ReproSteps",
        adversarial_input    AS "AdversarialInput",
        expected             AS "Expected",
        actual               AS "Actual",
        reference_comparison AS "ReferenceComparison",
        status               AS "Status",
        claim_actor_id       AS "ClaimActorId",
        claim_role           AS "ClaimRole",
        claim_token          AS "ClaimToken",
        claimed_at           AS "ClaimedAt",
        claimed_until        AS "ClaimedUntil",
        created_at           AS "CreatedAt",
        updated_at           AS "UpdatedAt"
        """;

    public async Task<Finding?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM findings WHERE id = @id LIMIT 1";
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { id }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async Task<Finding?> GetByClaimTokenAsync(Guid claimToken, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM findings WHERE claim_token = @claimToken LIMIT 1";
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { claimToken }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Finding>> ListByTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM findings WHERE task_id = @taskId ORDER BY created_at";
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { taskId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Finding>> ListByStatusAsync(Guid planId, IEnumerable<string> statuses, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            SELECT {Columns}
            FROM findings f
            JOIN tasks t ON t.id = f.task_id
            WHERE t.plan_id = @planId AND f.status = ANY(@statuses)
            ORDER BY f.created_at
            """;
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql,
            new { planId, statuses = statuses.ToArray() }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Finding>> InsertManyAsync(Guid taskId, IReadOnlyList<FindingInput> inputs, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return Array.Empty<Finding>();
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string sql = $"""
            INSERT INTO findings (
                task_id, external_key, severity, invariant_impact, symptom, root_cause,
                repro_steps, adversarial_input, expected, actual, reference_comparison, status
            )
            VALUES (
                @taskId, @ExternalKey, @Severity, @InvariantImpact, @Symptom, @RootCause,
                @ReproSteps, @AdversarialInput, @Expected, @Actual, @ReferenceComparison, 'pending_verification'
            )
            RETURNING {Columns}
            """;
        var results = new List<Finding>(inputs.Count);
        foreach (var input in inputs)
        {
            var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(sql,
                new
                {
                    taskId,
                    input.ExternalKey,
                    input.Severity,
                    input.InvariantImpact,
                    input.Symptom,
                    input.RootCause,
                    input.ReproSteps,
                    input.AdversarialInput,
                    input.Expected,
                    input.Actual,
                    input.ReferenceComparison,
                }, tx, cancellationToken: ct)).ConfigureAwait(false);
            results.Add(row.ToDomain());
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return results;
    }

    public async Task<ClaimedFinding?> ClaimNextForVerificationAsync(Guid planId, Guid actorId, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // Severity ordering: critical > high > medium > low > null. We use a CASE for a stable sort.
        var sql = $"""
            WITH next AS (
                SELECT f.id AS picked_id
                FROM findings f
                JOIN tasks t ON t.id = f.task_id
                WHERE t.plan_id = @planId
                  AND f.status = 'pending_verification'
                  AND (f.claim_token IS NULL OR f.claimed_until < now())
                ORDER BY CASE f.severity
                            WHEN 'critical' THEN 0
                            WHEN 'high'     THEN 1
                            WHEN 'medium'   THEN 2
                            WHEN 'low'      THEN 3
                            ELSE 4
                         END,
                         f.created_at ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE findings f
            SET claim_actor_id = @actorId,
                claim_role     = 'verifier',
                claim_token    = gen_random_uuid(),
                claimed_at     = now(),
                claimed_until  = now() + (@ttlSeconds || ' seconds')::interval
            FROM next
            WHERE f.id = next.picked_id
            RETURNING {Columns}
            """;
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql,
            new { planId, actorId, ttlSeconds = (int)ttl.TotalSeconds }, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null) return null;
        await EmitClaimedAsync(conn, row.Id, actorId, "verifier", ct).ConfigureAwait(false);
        var f = row.ToDomain();
        return new ClaimedFinding(f, f.Claim.Token!.Value);
    }

    public async Task<ClaimedFinding?> ClaimNextForFixAsync(Guid planId, Guid actorId, TimeSpan ttl, int retryCap, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            WITH next AS (
                SELECT f.id AS picked_id
                FROM findings f
                JOIN tasks t ON t.id = f.task_id
                WHERE t.plan_id = @planId
                  AND (
                       f.status = 'confirmed'
                       OR (f.status = 'fix_failed' AND
                           (SELECT COALESCE(COUNT(*),0) FROM attempts a WHERE a.finding_id = f.id) < @retryCap)
                       OR (f.status = 'partial'    AND
                           (SELECT COALESCE(COUNT(*),0) FROM attempts a WHERE a.finding_id = f.id) < @retryCap)
                  )
                  AND (f.claim_token IS NULL OR f.claimed_until < now())
                ORDER BY CASE f.severity
                            WHEN 'critical' THEN 0
                            WHEN 'high'     THEN 1
                            WHEN 'medium'   THEN 2
                            WHEN 'low'      THEN 3
                            ELSE 4
                         END,
                         f.created_at ASC
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE findings f
            SET claim_actor_id = @actorId,
                claim_role     = 'fixer',
                claim_token    = gen_random_uuid(),
                claimed_at     = now(),
                claimed_until  = now() + (@ttlSeconds || ' seconds')::interval,
                status         = 'in_fix'
            FROM next
            WHERE f.id = next.picked_id
            RETURNING {Columns}
            """;
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql,
            new { planId, actorId, ttlSeconds = (int)ttl.TotalSeconds, retryCap }, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null) return null;
        await EmitClaimedAsync(conn, row.Id, actorId, "fixer", ct).ConfigureAwait(false);
        var f = row.ToDomain();
        return new ClaimedFinding(f, f.Claim.Token!.Value);
    }

    public async Task<Finding?> UpdateStatusWithClaimAsync(
        Guid claimToken, Guid actorId, string newStatus, bool clearClaim,
        string eventType, string? eventPayloadJson, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var lockSql = $"SELECT {Columns} FROM findings WHERE claim_token = @claimToken FOR UPDATE";
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(lockSql,
            new { claimToken }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null || row.ClaimedUntil < DateTimeOffset.UtcNow)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        var updateSql = clearClaim
            ? $"""
              UPDATE findings
              SET status = @newStatus,
                  claim_actor_id = NULL, claim_role = NULL, claim_token = NULL,
                  claimed_at = NULL, claimed_until = NULL
              WHERE id = @id RETURNING {Columns}
              """
            : $"UPDATE findings SET status = @newStatus WHERE id = @id RETURNING {Columns}";
        var updated = await conn.QuerySingleAsync<Row>(new CommandDefinition(updateSql,
            new { newStatus, id = row.Id }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, from_state, to_state, payload)
            VALUES (@actorId, 'finding', @id, @eventType, @fromState, @toState, COALESCE(@payload::jsonb, '{}'::jsonb))
            """,
            new { actorId, id = row.Id, eventType, fromState = row.Status, toState = newStatus, payload = eventPayloadJson },
            tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return updated.ToDomain();
    }

    public async Task<Finding?> RefreshClaimAsync(
        Guid claimToken, Guid actorId, TimeSpan extendBy, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Same liveness gate as the task path: must match token + holding actor + still
        // be live. Expired claims fall through to release_claim + re-claim.
        var sql = $"""
            UPDATE findings
            SET claimed_until = now() + (@extendSeconds || ' seconds')::interval
            WHERE claim_token   = @claimToken
              AND claim_actor_id = @actorId
              AND claimed_until > now()
            RETURNING {Columns}
            """;
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql,
            new { claimToken, actorId, extendSeconds = (int)extendBy.TotalSeconds }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        if (row is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, payload)
            VALUES (@actorId, 'finding', @id, 'claim_refreshed',
                    jsonb_build_object('extend_seconds', @extendSeconds, 'claimed_until', @until))
            """, new { actorId, id = row.Id, extendSeconds = (int)extendBy.TotalSeconds, until = row.ClaimedUntil }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row.ToDomain();
    }

    public async Task<Finding?> OverrideStatusAsync(
        Guid findingId, Guid actorId, string newStatus, string reason, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var lockSql = $"SELECT {Columns} FROM findings WHERE id = @findingId FOR UPDATE";
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(lockSql,
            new { findingId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        var updateSql = $"""
            UPDATE findings
            SET status = @newStatus,
                claim_actor_id = NULL, claim_role = NULL, claim_token = NULL,
                claimed_at = NULL, claimed_until = NULL
            WHERE id = @findingId
            RETURNING {Columns}
            """;
        var updated = await conn.QuerySingleAsync<Row>(new CommandDefinition(updateSql,
            new { newStatus, findingId }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, from_state, to_state, payload)
            VALUES (@actorId, 'finding', @findingId, 'override', @fromState, @toState, jsonb_build_object('reason', @reason))
            """,
            new { actorId, findingId, fromState = row.Status, toState = newStatus, reason },
            tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return updated.ToDomain();
    }

    public async Task<int> SweepExpiredClaimsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition("""
            WITH stale AS (
                SELECT id FROM findings
                WHERE claim_token IS NOT NULL AND claimed_until < now() AND status IN ('in_fix','pending_verification')
                FOR UPDATE SKIP LOCKED
            )
            UPDATE findings f
            SET claim_actor_id = NULL,
                claim_role     = NULL,
                claim_token    = NULL,
                claimed_at     = NULL,
                claimed_until  = NULL,
                status         = CASE WHEN f.status = 'in_fix' THEN 'confirmed' ELSE f.status END
            FROM stale
            WHERE f.id = stale.id
            """, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> AllInTerminalResolvedAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // Every finding on the task is in a terminal state where "resolved" excludes rejected
        // OR includes it (rejected means the finding wasn't real, which is a clean resolution).
        var anyOpen = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM findings
                WHERE task_id = @taskId
                  AND status NOT IN ('fixed','rejected','needs_human_review','deferred')
            )
            """, new { taskId }, cancellationToken: ct)).ConfigureAwait(false);
        return !anyOpen;
    }

    private static async Task EmitClaimedAsync(Npgsql.NpgsqlConnection conn, Guid id, Guid actorId, string role, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, payload)
            VALUES (@actorId, 'finding', @id, 'claimed', jsonb_build_object('role', @role))
            """, new { actorId, id, role }, cancellationToken: ct)).ConfigureAwait(false);
    }

    // Property-init for Dapper's lenient hydration path (same rationale as TaskRow).
    internal sealed record Row
    {
        public Guid    Id                  { get; init; }
        public Guid    TaskId              { get; init; }
        public string  ExternalKey         { get; init; } = "";
        public string? Severity            { get; init; }
        public string? InvariantImpact     { get; init; }
        public string? Symptom             { get; init; }
        public string? RootCause           { get; init; }
        public string? ReproSteps          { get; init; }
        public string? AdversarialInput    { get; init; }
        public string? Expected            { get; init; }
        public string? Actual              { get; init; }
        public string? ReferenceComparison { get; init; }
        public string  Status              { get; init; } = "";
        public Guid?   ClaimActorId        { get; init; }
        public string? ClaimRole           { get; init; }
        public Guid?   ClaimToken          { get; init; }
        public DateTimeOffset? ClaimedAt   { get; init; }
        public DateTimeOffset? ClaimedUntil{ get; init; }
        public DateTimeOffset  CreatedAt   { get; init; }
        public DateTimeOffset  UpdatedAt   { get; init; }

        public Finding ToDomain() => new(
            Id, TaskId, ExternalKey, Severity, InvariantImpact, Symptom, RootCause,
            ReproSteps, AdversarialInput, Expected, Actual, ReferenceComparison,
            Status,
            new ClaimState(ClaimActorId, ClaimRole, ClaimToken, ClaimedAt, ClaimedUntil),
            CreatedAt, UpdatedAt);
    }
}
