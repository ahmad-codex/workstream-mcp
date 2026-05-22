using System;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IUserRepository
{
    Task<User?> ResolveByTokenAsync(string token, CancellationToken ct = default);
    Task<User?> GetAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>
    /// The user's raw MCP URL token, for an active user — used by the audit dispatcher to
    /// run an audit on the triggering user's behalf. Null for an unknown or inactive
    /// user. Treat the result as a credential: never log it.
    /// </summary>
    Task<string?> GetMcpTokenAsync(Guid userId, CancellationToken ct = default);
    Task<User> CreateAsync(string githubUsername, string? displayName, string actorType, string token, bool isAdmin, CancellationToken ct = default);
    Task<User?> RotateTokenAsync(Guid userId, string newToken, string oldTokenHash, TimeSpan revokedFor, CancellationToken ct = default);
    Task<int> RevokeAsync(Guid userId, string oldTokenHash, TimeSpan revokedFor, CancellationToken ct = default);
    Task SetPermissionAsync(Guid userId, string permission, bool value, CancellationToken ct = default);
    Task RecordTokenUsageAsync(Guid userId, string toolName, bool success, string? ip, CancellationToken ct = default);
    Task<bool> IsTokenRevokedAsync(string tokenHash, CancellationToken ct = default);
}
