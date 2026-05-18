using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public sealed class ProjectRepository : IProjectRepository
{
    private readonly IDbConnectionFactory _factory;
    public ProjectRepository(IDbConnectionFactory factory) => _factory = factory;

    private const string ProjectColumns = """
        id AS "Id",
        organization_id AS "OrganizationId",
        slug AS "Slug",
        display_name AS "DisplayName",
        description AS "Description",
        config::text AS "Config",
        created_at AS "CreatedAt"
        """;

    private const string RepoColumns = """
        id AS "Id",
        project_id AS "ProjectId",
        github_owner AS "GithubOwner",
        github_repo AS "GithubRepo",
        default_branch AS "DefaultBranch",
        is_reference_only AS "IsReferenceOnly",
        created_at AS "CreatedAt"
        """;

    private const string BoardColumns = """
        id AS "Id",
        project_id AS "ProjectId",
        github_project_v2_node_id AS "GithubProjectV2NodeId",
        github_project_number AS "GithubProjectNumber",
        github_owner AS "GithubOwner",
        display_name AS "DisplayName",
        status_field_node_id AS "StatusFieldNodeId",
        status_option_backlog AS "StatusOptionBacklog",
        status_option_in_progress AS "StatusOptionInProgress",
        status_option_review AS "StatusOptionReview",
        status_option_done AS "StatusOptionDone",
        status_option_blocked AS "StatusOptionBlocked",
        config::text AS "Config",
        created_at AS "CreatedAt"
        """;

    private const string SlackColumns = """
        project_id AS "ProjectId",
        workspace_id AS "WorkspaceId",
        bot_token_secret_ref AS "BotTokenSecretRef",
        default_channel_id AS "DefaultChannelId",
        notify_on AS "NotifyOn",
        created_at AS "CreatedAt"
        """;

    public async Task<Project?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {ProjectColumns} FROM projects WHERE id = @id";
        return await conn.QuerySingleOrDefaultAsync<Project>(new CommandDefinition(sql, new { id }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<Project?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {ProjectColumns} FROM projects WHERE slug = @slug";
        return await conn.QuerySingleOrDefaultAsync<Project>(new CommandDefinition(sql, new { slug }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {ProjectColumns} FROM projects ORDER BY slug";
        var rows = await conn.QueryAsync<Project>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<Project> CreateAsync(string slug, string displayName, string? description, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            INSERT INTO projects (slug, display_name, description) VALUES (@slug, @displayName, @description)
            RETURNING {ProjectColumns}
            """;
        return await conn.QuerySingleAsync<Project>(new CommandDefinition(sql,
            new { slug, displayName, description }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<Guid> AddRepoAsync(Guid projectId, string owner, string repo, bool referenceOnly, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO project_repos (project_id, github_owner, github_repo, is_reference_only)
            VALUES (@projectId, @owner, @repo, @referenceOnly) RETURNING id
            """, new { projectId, owner, repo, referenceOnly }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectRepo>> ListReposAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {RepoColumns} FROM project_repos WHERE project_id = @projectId ORDER BY github_owner, github_repo";
        var rows = await conn.QueryAsync<ProjectRepo>(new CommandDefinition(sql, new { projectId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<Guid> AddBoardAsync(ProjectBoard board, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO project_boards (
                project_id, github_project_v2_node_id, github_project_number, github_owner,
                display_name, status_field_node_id,
                status_option_backlog, status_option_in_progress, status_option_review, status_option_done, status_option_blocked,
                config
            ) VALUES (
                @ProjectId, @GithubProjectV2NodeId, @GithubProjectNumber, @GithubOwner,
                @DisplayName, @StatusFieldNodeId,
                @StatusOptionBacklog, @StatusOptionInProgress, @StatusOptionReview, @StatusOptionDone, @StatusOptionBlocked,
                @Config::jsonb
            )
            RETURNING id
            """, board, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectBoard>> ListBoardsAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {BoardColumns} FROM project_boards WHERE project_id = @projectId ORDER BY display_name";
        var rows = await conn.QueryAsync<ProjectBoard>(new CommandDefinition(sql, new { projectId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<ProjectBoard?> GetBoardAsync(Guid boardId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {BoardColumns} FROM project_boards WHERE id = @boardId";
        return await conn.QuerySingleOrDefaultAsync<ProjectBoard>(new CommandDefinition(sql, new { boardId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task SetSlackAsync(Guid projectId, string workspaceId, string botTokenSecretRef, string defaultChannelId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO project_slack (project_id, workspace_id, bot_token_secret_ref, default_channel_id)
            VALUES (@projectId, @workspaceId, @botTokenSecretRef, @defaultChannelId)
            ON CONFLICT (project_id) DO UPDATE
              SET workspace_id          = EXCLUDED.workspace_id,
                  bot_token_secret_ref  = EXCLUDED.bot_token_secret_ref,
                  default_channel_id    = EXCLUDED.default_channel_id
            """, new { projectId, workspaceId, botTokenSecretRef, defaultChannelId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<ProjectSlack?> GetSlackAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {SlackColumns} FROM project_slack WHERE project_id = @projectId";
        return await conn.QuerySingleOrDefaultAsync<ProjectSlack>(new CommandDefinition(sql, new { projectId }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
