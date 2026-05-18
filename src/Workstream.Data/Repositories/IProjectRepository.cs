using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IProjectRepository
{
    Task<Project?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Project?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default);
    Task<Project> CreateAsync(string slug, string displayName, string? description, CancellationToken ct = default);
    Task<Guid> AddRepoAsync(Guid projectId, string owner, string repo, bool referenceOnly, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectRepo>> ListReposAsync(Guid projectId, CancellationToken ct = default);
    Task<Guid> AddBoardAsync(ProjectBoard board, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectBoard>> ListBoardsAsync(Guid projectId, CancellationToken ct = default);
    Task<ProjectBoard?> GetBoardAsync(Guid boardId, CancellationToken ct = default);
    Task SetSlackAsync(Guid projectId, string workspaceId, string botTokenSecretRef, string defaultChannelId, CancellationToken ct = default);
    Task<ProjectSlack?> GetSlackAsync(Guid projectId, CancellationToken ct = default);
}
