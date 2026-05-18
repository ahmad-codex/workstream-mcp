using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public sealed class EventRepository : IEventRepository
{
    private readonly IDbConnectionFactory _factory;
    public EventRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string Columns = """
        id            AS "Id",
        at            AS "At",
        actor_id      AS "ActorId",
        entity_type   AS "EntityType",
        entity_id     AS "EntityId",
        event_type    AS "EventType",
        from_state    AS "FromState",
        to_state      AS "ToState",
        payload::text AS "Payload"
        """;

    public async Task EmitAsync(Guid actorId, string entityType, Guid entityId, string eventType, string? fromState, string? toState, string? payloadJson, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (actor_id, entity_type, entity_id, event_type, from_state, to_state, payload)
            VALUES (@actorId, @entityType, @entityId, @eventType, @fromState, @toState, COALESCE(@payload::jsonb, '{}'::jsonb))
            """, new { actorId, entityType, entityId, eventType, fromState, toState, payload = payloadJson }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Event>> ListForEntityAsync(string entityType, Guid entityId, DateTimeOffset? since, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            SELECT {Columns}
            FROM events
            WHERE entity_type = @entityType AND entity_id = @entityId
              AND (@since IS NULL OR at > @since)
            ORDER BY at DESC, id DESC
            LIMIT @limit
            """;
        var rows = await conn.QueryAsync<Event>(new CommandDefinition(sql, new { entityType, entityId, since, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<Event>> ListRecentForPlanAsync(Guid planId, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // Walk both directly-attached entities (events on the plan itself) AND events on the
        // plan's tasks/findings/attempts. Aggregated query.
        var sql = $"""
            WITH plan_entities AS (
                SELECT 'task'::text     AS et, t.id AS eid FROM tasks    t WHERE t.plan_id = @planId
                UNION ALL
                SELECT 'finding'::text  AS et, f.id AS eid FROM findings f JOIN tasks t ON t.id = f.task_id WHERE t.plan_id = @planId
                UNION ALL
                SELECT 'attempt'::text  AS et, a.id AS eid
                  FROM attempts a
                  LEFT JOIN findings f ON f.id = a.finding_id
                  LEFT JOIN tasks    t ON t.id = COALESCE(a.task_id, f.task_id)
                  WHERE t.plan_id = @planId
                UNION ALL
                SELECT 'plan'::text     AS et, @planId AS eid
            )
            SELECT {Columns}
            FROM events e
            JOIN plan_entities pe ON pe.et = e.entity_type AND pe.eid = e.entity_id
            ORDER BY e.at DESC, e.id DESC
            LIMIT @limit
            """;
        var rows = await conn.QueryAsync<Event>(new CommandDefinition(sql, new { planId, limit }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }
}
