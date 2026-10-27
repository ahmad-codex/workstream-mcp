using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Workstream.Core.Errors;

namespace Workstream.Data;

/// <summary>
/// Optional per-role limits on <c>refresh_claim</c>, from <c>claim_limits</c> in the plan type's
/// config. Null fields mean no limit, which is the behaviour without config.
/// </summary>
public sealed record ClaimRefreshLimits(TimeSpan? MaxLifetime, bool RequireProgress);

/// <summary>
/// Checks a live claim against <see cref="ClaimRefreshLimits"/> before a refresh, inside the
/// refresh transaction. Throws <c>claim_limit_reached</c> when the refresh is refused; the claim
/// itself is left alone and runs out on its current TTL.
/// </summary>
internal static class ClaimRefreshGate
{
    // Progress is any event the holder wrote on the entity or its children since the claim or
    // the last refresh, other than claiming and refreshing.
    private const string TaskProgressSql = """
        SELECT EXISTS (
            SELECT 1 FROM events e
            WHERE e.actor_id = @actorId
              AND e.at > (SELECT COALESCE(max(r.at), @claimedAt) FROM events r
                          WHERE r.entity_type = @entityType AND r.entity_id = @id
                            AND r.event_type = 'claim_refreshed' AND r.at >= @claimedAt)
              AND e.event_type NOT IN ('claimed', 'claim_refreshed')
              AND ((e.entity_type = 'task' AND e.entity_id = @id)
                OR (e.entity_type = 'attempt' AND e.entity_id IN (SELECT id FROM attempts WHERE task_id = @id))
                OR (e.entity_type = 'finding' AND e.entity_id IN (SELECT id FROM findings WHERE task_id = @id))))
        """;

    private const string FindingProgressSql = """
        SELECT EXISTS (
            SELECT 1 FROM events e
            WHERE e.actor_id = @actorId
              AND e.at > (SELECT COALESCE(max(r.at), @claimedAt) FROM events r
                          WHERE r.entity_type = @entityType AND r.entity_id = @id
                            AND r.event_type = 'claim_refreshed' AND r.at >= @claimedAt)
              AND e.event_type NOT IN ('claimed', 'claim_refreshed')
              AND ((e.entity_type = 'finding' AND e.entity_id = @id)
                OR (e.entity_type = 'attempt' AND e.entity_id IN (SELECT id FROM attempts WHERE finding_id = @id))))
        """;

    /// <summary>Returns the latest <c>claimed_until</c> the refresh may set, or null for no cap.</summary>
    public static async Task<DateTimeOffset?> CheckAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string entityType, Guid id, Guid actorId,
        DateTimeOffset claimedAt, DateTimeOffset claimedUntil, ClaimRefreshLimits? limits, CancellationToken ct)
    {
        if (limits is null) return null;

        DateTimeOffset? cap = limits.MaxLifetime is { } max ? claimedAt.Add(max) : null;
        if (cap is { } c && claimedUntil >= c)
            throw Refused("max_lifetime",
                "the claim has reached its maximum lifetime and can't be extended; submit, release, or let it expire",
                claimedAt, claimedUntil, cap);

        if (limits.RequireProgress)
        {
            var progressed = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                entityType == "task" ? TaskProgressSql : FindingProgressSql,
                new { actorId, claimedAt, entityType, id }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (!progressed)
                throw Refused("no_progress",
                    "no progress recorded since the claim or the last refresh; record an attempt, findings, " +
                    "a commit or a status change before refreshing again",
                    claimedAt, claimedUntil, cap);
        }

        return cap;
    }

    private static WorkstreamException Refused(
        string reason, string message, DateTimeOffset claimedAt, DateTimeOffset claimedUntil, DateTimeOffset? cap)
        => new(new WorkstreamError(ErrorCodes.ClaimLimitReached, message, new Dictionary<string, object?>
        {
            ["reason"]        = reason,
            ["claimed_at"]    = claimedAt,
            ["claimed_until"] = claimedUntil,
            ["max_until"]     = cap,
        }));
}
