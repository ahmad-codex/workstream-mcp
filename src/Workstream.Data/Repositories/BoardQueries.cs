using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Workstream.Data.Repositories;

public sealed class BoardQueries : IBoardQueries
{
    private readonly IDbConnectionFactory _factory;
    public BoardQueries(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<BoardTask>> ListTasksAsync(Guid planId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<BoardTask>(new CommandDefinition("""
            SELECT t.id                                   AS "Id",
                   t.external_key                         AS "ExternalKey",
                   t.title                                AS "Title",
                   t.status                               AS "Status",
                   t.priority                             AS "Priority",
                   ph.name                                AS "Phase",
                   t.claim_actor_id                       AS "ClaimActorId",
                   COALESCE(u.display_name, u.github_username::text) AS "ClaimActor",
                   t.claim_role                           AS "ClaimRole",
                   t.claimed_at                           AS "ClaimedAt",
                   t.claimed_until                        AS "ClaimedUntil",
                   COALESCE((SELECT array_agg(td.depends_on_task_id ORDER BY td.created_at)
                               FROM task_dependencies td WHERE td.task_id = t.id), '{}') AS "DependsOn",
                   t.github_issue_number                  AS "GithubIssue",
                   t.updated_at                           AS "UpdatedAt"
            FROM tasks t
            LEFT JOIN phases ph ON ph.id = t.phase_id
            LEFT JOIN users  u  ON u.id  = t.claim_actor_id
            WHERE t.plan_id = @planId
            ORDER BY t.priority DESC, t.created_at
            """, new { planId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<BoardFinding>> ListFindingsAsync(Guid planId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<BoardFinding>(new CommandDefinition("""
            SELECT f.id                                   AS "Id",
                   f.task_id                              AS "TaskId",
                   f.external_key                         AS "ExternalKey",
                   f.severity                             AS "Severity",
                   f.symptom                              AS "Symptom",
                   f.status                               AS "Status",
                   COALESCE(u.display_name, u.github_username::text) AS "ClaimActor",
                   f.claim_role                           AS "ClaimRole",
                   f.claimed_until                        AS "ClaimedUntil",
                   f.updated_at                           AS "UpdatedAt"
            FROM findings f
            JOIN tasks t ON t.id = f.task_id
            LEFT JOIN users u ON u.id = f.claim_actor_id
            WHERE t.plan_id = @planId
            ORDER BY f.created_at
            """, new { planId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetActorNamesAsync(IEnumerable<Guid> actorIds, CancellationToken ct = default)
    {
        var ids = actorIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, string>();
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(Guid Id, string Name)>(new CommandDefinition("""
            SELECT id AS Id, COALESCE(display_name, github_username::text) AS Name
            FROM users WHERE id = ANY(@ids)
            """, new { ids }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }
}
