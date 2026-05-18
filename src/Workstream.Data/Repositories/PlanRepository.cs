using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public sealed class PlanRepository : IPlanRepository
{
    private readonly IDbConnectionFactory _factory;
    public PlanRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string PlanColumns = """
        id AS "Id",
        project_id AS "ProjectId",
        plan_type_id AS "PlanTypeId",
        name AS "Name",
        objective AS "Objective",
        status AS "Status",
        created_by_actor_id AS "CreatedByActorId",
        primary_board_id AS "PrimaryBoardId",
        primary_slack_channel_id AS "PrimarySlackChannelId",
        config::text AS "Config",
        created_at AS "CreatedAt",
        activated_at AS "ActivatedAt",
        completed_at AS "CompletedAt"
        """;

    private const string PhaseColumns = """
        id AS "Id",
        plan_id AS "PlanId",
        order_index AS "OrderIndex",
        name AS "Name",
        status AS "Status",
        created_at AS "CreatedAt"
        """;

    public async Task<Plan?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {PlanColumns} FROM plans WHERE id = @id";
        return await conn.QuerySingleOrDefaultAsync<Plan>(new CommandDefinition(sql, new { id }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Plan>> ListByProjectAsync(Guid projectId, string? statusFilter, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = statusFilter is null
            ? $"SELECT {PlanColumns} FROM plans WHERE project_id = @projectId ORDER BY created_at DESC"
            : $"SELECT {PlanColumns} FROM plans WHERE project_id = @projectId AND status = @statusFilter ORDER BY created_at DESC";
        var rows = await conn.QueryAsync<Plan>(new CommandDefinition(sql, new { projectId, statusFilter }, cancellationToken: ct))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<Plan> CreateAsync(Guid projectId, string planTypeId, string name, string? objective,
        Guid? createdByActorId, Guid? primaryBoardId, string? primarySlackChannelId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            INSERT INTO plans (project_id, plan_type_id, name, objective, status, created_by_actor_id, primary_board_id, primary_slack_channel_id)
            VALUES (@projectId, @planTypeId, @name, @objective, 'draft', @createdByActorId, @primaryBoardId, @primarySlackChannelId)
            RETURNING {PlanColumns}
            """;
        return await conn.QuerySingleAsync<Plan>(new CommandDefinition(sql,
            new { projectId, planTypeId, name, objective, createdByActorId, primaryBoardId, primarySlackChannelId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<Plan?> SetStatusAsync(Guid id, string newStatus, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var stamp = newStatus switch
        {
            PlanStatus.Active    => ", activated_at = COALESCE(activated_at, now())",
            PlanStatus.Completed => ", completed_at = now()",
            _                    => "",
        };
        var sql = $"UPDATE plans SET status = @newStatus{stamp} WHERE id = @id RETURNING {PlanColumns}";
        return await conn.QuerySingleOrDefaultAsync<Plan>(new CommandDefinition(sql, new { id, newStatus }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<Guid> AddPhaseAsync(Guid planId, int orderIndex, string name, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO phases (plan_id, order_index, name) VALUES (@planId, @orderIndex, @name) RETURNING id
            """, new { planId, orderIndex, name }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Phase>> ListPhasesAsync(Guid planId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {PhaseColumns} FROM phases WHERE plan_id = @planId ORDER BY order_index";
        var rows = await conn.QueryAsync<Phase>(new CommandDefinition(sql, new { planId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<TaskCountsByStatus> GetTaskCountsAsync(Guid planId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(string Status, int N)>(new CommandDefinition(
            "SELECT status, COUNT(*) AS n FROM tasks WHERE plan_id = @planId GROUP BY status",
            new { planId }, cancellationToken: ct)).ConfigureAwait(false);
        var dict = rows.ToDictionary(r => r.Status, r => r.N, StringComparer.Ordinal);
        return new TaskCountsByStatus(dict);
    }
}
