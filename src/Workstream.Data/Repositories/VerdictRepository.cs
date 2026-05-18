using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public sealed class VerdictRepository : IVerdictRepository
{
    private readonly IDbConnectionFactory _factory;
    public VerdictRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string Columns = """
        id AS "Id",
        finding_id AS "FindingId",
        attempt_id AS "AttemptId",
        verdict_type AS "VerdictType",
        reason_category AS "ReasonCategory",
        pre_output AS "PreOutput",
        post_output AS "PostOutput",
        adversarial_output AS "AdversarialOutput",
        invariant_evidence::text AS "InvariantEvidence",
        actor_id AS "ActorId",
        at AS "At"
        """;

    public Task<Verdict> InsertForFindingAsync(Guid findingId, Guid actorId, string verdictType, VerdictEvidence ev, CancellationToken ct = default)
        => InsertCoreAsync(findingId, null, actorId, verdictType, ev, ct);

    public Task<Verdict> InsertForAttemptAsync(Guid attemptId, Guid actorId, string verdictType, VerdictEvidence ev, CancellationToken ct = default)
        => InsertCoreAsync(null, attemptId, actorId, verdictType, ev, ct);

    private async Task<Verdict> InsertCoreAsync(Guid? findingId, Guid? attemptId, Guid actorId, string verdictType, VerdictEvidence ev, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            INSERT INTO verdicts (
                finding_id, attempt_id, verdict_type, reason_category, pre_output, post_output, adversarial_output, invariant_evidence, actor_id
            )
            VALUES (
                @findingId, @attemptId, @verdictType, @ReasonCategory, @PreOutput, @PostOutput, @AdversarialOutput, @InvariantEvidence::jsonb, @actorId
            )
            RETURNING {Columns}
            """;
        return await conn.QuerySingleAsync<Verdict>(new CommandDefinition(sql, new
        {
            findingId,
            attemptId,
            verdictType,
            actorId,
            ev.ReasonCategory,
            ev.PreOutput,
            ev.PostOutput,
            ev.AdversarialOutput,
            ev.InvariantEvidence,
        }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Verdict>> ListForFindingAsync(Guid findingId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM verdicts WHERE finding_id = @findingId ORDER BY at DESC";
        var rows = await conn.QueryAsync<Verdict>(new CommandDefinition(sql, new { findingId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Verdict>> ListForAttemptAsync(Guid attemptId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM verdicts WHERE attempt_id = @attemptId ORDER BY at DESC";
        var rows = await conn.QueryAsync<Verdict>(new CommandDefinition(sql, new { attemptId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }
}
