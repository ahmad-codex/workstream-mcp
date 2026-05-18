using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _factory;
    public UserRepository(IDbConnectionFactory factory) => _factory = factory;

    // Cast non-builtin column types to text/builtin so Npgsql can hydrate them
    // when running with `Server Compatibility Mode=NoTypeLoading` (set so the
    // pool survives a postgres recreate behind pgbouncer). Without the cast
    // Dapper's GetColumnHash → reader.GetFieldType call trips on the unknown
    // OID with "Reading as 'System.Object' is not supported for fields having
    // DataTypeName '-'". citext is from the citext extension; config is jsonb.
    private const string Columns = """
        id AS "Id",
        github_username::text AS "GithubUsername",
        display_name AS "DisplayName",
        actor_type AS "ActorType",
        is_active AS "IsActive",
        can_override_verdict AS "CanOverrideVerdict",
        can_archive_plan AS "CanArchivePlan",
        can_mark_needs_human_review AS "CanMarkNeedsHumanReview",
        is_admin AS "IsAdmin",
        config::text AS "Config",
        created_at AS "CreatedAt",
        last_seen_at AS "LastSeenAt"
        """;

    public async Task<User?> ResolveByTokenAsync(string token, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            UPDATE users SET last_seen_at = now()
            WHERE mcp_url_token = @token AND is_active = true
            RETURNING {Columns}
            """;
        return await conn.QuerySingleOrDefaultAsync<User>(new CommandDefinition(sql, new { token }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<User?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM users WHERE id = @id";
        return await conn.QuerySingleOrDefaultAsync<User>(new CommandDefinition(sql, new { id }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"SELECT {Columns} FROM users WHERE github_username = @username";
        return await conn.QuerySingleOrDefaultAsync<User>(new CommandDefinition(sql, new { username }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<User> CreateAsync(string githubUsername, string? displayName, string actorType, string token, bool isAdmin, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        var sql = $"""
            INSERT INTO users (github_username, display_name, mcp_url_token, actor_type, is_admin)
            VALUES (@githubUsername, @displayName, @token, @actorType, @isAdmin)
            RETURNING {Columns}
            """;
        return await conn.QuerySingleAsync<User>(new CommandDefinition(sql,
            new { githubUsername, displayName, token, actorType, isAdmin }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task<User?> RotateTokenAsync(Guid userId, string newToken, string oldTokenHash, TimeSpan revokedFor, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO revoked_tokens (token_hash, user_id, expires_at)
            VALUES (@oldTokenHash, @userId, now() + (@seconds || ' seconds')::interval)
            ON CONFLICT (token_hash) DO NOTHING
            """, new { oldTokenHash, userId, seconds = (int)revokedFor.TotalSeconds }, tx, cancellationToken: ct))
            .ConfigureAwait(false);

        var sql = $"""
            UPDATE users SET mcp_url_token = @newToken WHERE id = @userId RETURNING {Columns}
            """;
        var user = await conn.QuerySingleOrDefaultAsync<User>(new CommandDefinition(sql,
            new { userId, newToken }, tx, cancellationToken: ct)).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return user;
    }

    public async Task<int> RevokeAsync(Guid userId, string oldTokenHash, TimeSpan revokedFor, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO revoked_tokens (token_hash, user_id, expires_at)
            VALUES (@oldTokenHash, @userId, now() + (@seconds || ' seconds')::interval)
            ON CONFLICT (token_hash) DO NOTHING
            """, new { oldTokenHash, userId, seconds = (int)revokedFor.TotalSeconds }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET is_active = false WHERE id = @userId", new { userId }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return n;
    }

    public async Task SetPermissionAsync(Guid userId, string permission, bool value, CancellationToken ct = default)
    {
        var column = permission switch
        {
            "can_override_verdict"        => "can_override_verdict",
            "can_archive_plan"            => "can_archive_plan",
            "can_mark_needs_human_review" => "can_mark_needs_human_review",
            "is_admin"                    => "is_admin",
            _ => throw new ArgumentException($"unknown permission '{permission}'"),
        };
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        // Whitelisted column name above means it is safe to interpolate here.
        var sql = $"UPDATE users SET {column} = @value WHERE id = @userId";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userId, value }, cancellationToken: ct))
            .ConfigureAwait(false);
    }

    public async Task RecordTokenUsageAsync(Guid userId, string toolName, bool success, string? ip, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO token_usage (user_id, tool_name, success, ip)
            VALUES (@userId, @toolName, @success, @ip::inet)
            """, new { userId, toolName, success, ip }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> IsTokenRevokedAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM revoked_tokens WHERE token_hash = @hash AND expires_at > now())",
            new { hash = tokenHash }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
