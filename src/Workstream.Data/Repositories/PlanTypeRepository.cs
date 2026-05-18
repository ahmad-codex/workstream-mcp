using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public sealed class PlanTypeRepository : IPlanTypeRepository
{
    private readonly IDbConnectionFactory _factory;

    public PlanTypeRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string SelectColumns = """
        id,
        display_name AS "DisplayName",
        state_graph::text AS "StateGraphJson",
        role_ttls::text AS "RoleTtlsJson",
        retry_cap AS "RetryCap",
        prompt_template_ref AS "PromptTemplateRef",
        requires_findings AS "RequiresFindings",
        board_column_mapping::text AS "BoardColumnMappingJson",
        config::text AS "ConfigJson",
        created_at AS "CreatedAt",
        updated_at AS "UpdatedAt"
        """;

    public async Task<PlanType?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {SelectColumns} FROM plan_types WHERE id = @id LIMIT 1";
        return await conn.QuerySingleOrDefaultAsync<PlanType>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PlanType>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {SelectColumns} FROM plan_types ORDER BY id";
        var rows = await conn.QueryAsync<PlanType>(new CommandDefinition(sql, cancellationToken: ct));
        return (IReadOnlyList<PlanType>)rows.AsList();
    }
}
