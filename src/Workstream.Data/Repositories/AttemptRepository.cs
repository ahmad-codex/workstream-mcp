using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public sealed class AttemptRepository : IAttemptRepository
{
    private readonly IDbConnectionFactory _factory;
    public AttemptRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string Columns = """
        id                AS "Id",
        finding_id        AS "FindingId",
        task_id           AS "TaskId",
        attempt_number    AS "AttemptNumber",
        files_changed     AS "FilesChanged",
        approach_summary  AS "ApproachSummary",
        side_effects      AS "SideEffects",
        build_command     AS "BuildCommand",
        test_scenario     AS "TestScenario",
        diff_ref          AS "DiffRef",
        commit_hash       AS "CommitHash",
        actor_id          AS "ActorId",
        claim_actor_id    AS "ClaimActorId",
        claim_role        AS "ClaimRole",
        claim_token       AS "ClaimToken",
        claimed_at        AS "ClaimedAt",
        claimed_until     AS "ClaimedUntil",
        created_at        AS "CreatedAt"
        """;

    public async Task<Attempt?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM attempts WHERE id = @id LIMIT 1";
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { id }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDomain();
    }

    public async Task<int> CountForFindingAsync(Guid findingId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM attempts WHERE finding_id = @findingId", new { findingId }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<int> CountForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM attempts WHERE task_id = @taskId", new { taskId }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Attempt>> ListForFindingAsync(Guid findingId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM attempts WHERE finding_id = @findingId ORDER BY attempt_number";
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { findingId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Attempt>> ListForTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM attempts WHERE task_id = @taskId ORDER BY attempt_number";
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { taskId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public Task<Attempt> InsertForFindingAsync(Guid findingId, Guid actorId, AttemptInput input, CancellationToken ct = default)
        => InsertCoreAsync(findingId: findingId, taskId: null, actorId, input, ct);

    public Task<Attempt> InsertForTaskAsync(Guid taskId, Guid actorId, AttemptInput input, CancellationToken ct = default)
        => InsertCoreAsync(findingId: null, taskId: taskId, actorId, input, ct);

    private async Task<Attempt> InsertCoreAsync(Guid? findingId, Guid? taskId, Guid actorId, AttemptInput input, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Atomically pick the next attempt_number.
        var next = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            findingId is not null
                ? "SELECT COALESCE(MAX(attempt_number), 0) + 1 FROM attempts WHERE finding_id = @fid"
                : "SELECT COALESCE(MAX(attempt_number), 0) + 1 FROM attempts WHERE task_id    = @tid",
            new { fid = findingId, tid = taskId }, tx, cancellationToken: ct)).ConfigureAwait(false);

        var sql = $"""
            INSERT INTO attempts (
                finding_id, task_id, attempt_number,
                files_changed, approach_summary, side_effects, build_command, test_scenario, diff_ref, actor_id
            )
            VALUES (
                @findingId, @taskId, @next,
                @FilesChanged, @ApproachSummary, @SideEffects, @BuildCommand, @TestScenario, @DiffRef, @actorId
            )
            RETURNING {Columns}
            """;
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(sql, new
        {
            findingId,
            taskId,
            next,
            input.FilesChanged,
            input.ApproachSummary,
            input.SideEffects,
            input.BuildCommand,
            input.TestScenario,
            input.DiffRef,
            actorId,
        }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, payload)
            VALUES (@actorId, 'attempt', @id, 'attempt_submitted',
                    jsonb_build_object('attempt_number', @num))
            """, new { actorId, id = row.Id, num = row.AttemptNumber }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row.ToDomain();
    }

    public async Task<Attempt?> SetCommitHashAsync(Guid attemptId, Guid actorId, string commitHash, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var sql = $"""
            UPDATE attempts SET commit_hash = @commitHash WHERE id = @attemptId RETURNING {Columns}
            """;
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql,
            new { attemptId, commitHash }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, payload)
            VALUES (@actorId, 'attempt', @attemptId, 'commit_recorded', jsonb_build_object('commit', @commitHash))
            """, new { actorId, attemptId, commitHash }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row.ToDomain();
    }

    // Property-init for Dapper's lenient hydration path.
    internal sealed record Row
    {
        public Guid    Id              { get; init; }
        public Guid?   FindingId       { get; init; }
        public Guid?   TaskId          { get; init; }
        public int     AttemptNumber   { get; init; }
        public string[]? FilesChanged  { get; init; }
        public string? ApproachSummary { get; init; }
        public string? SideEffects     { get; init; }
        public string? BuildCommand    { get; init; }
        public string? TestScenario    { get; init; }
        public string? DiffRef         { get; init; }
        public string? CommitHash      { get; init; }
        public Guid?   ActorId         { get; init; }
        public Guid?   ClaimActorId    { get; init; }
        public string? ClaimRole       { get; init; }
        public Guid?   ClaimToken      { get; init; }
        public DateTimeOffset? ClaimedAt    { get; init; }
        public DateTimeOffset? ClaimedUntil { get; init; }
        public DateTimeOffset  CreatedAt    { get; init; }

        public Attempt ToDomain() => new(
            Id, FindingId, TaskId, AttemptNumber,
            FilesChanged, ApproachSummary, SideEffects, BuildCommand, TestScenario, DiffRef, CommitHash,
            ActorId,
            new ClaimState(ClaimActorId, ClaimRole, ClaimToken, ClaimedAt, ClaimedUntil),
            CreatedAt);
    }
}
