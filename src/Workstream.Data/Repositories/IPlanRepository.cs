using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IPlanRepository
{
    Task<Plan?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Plan>> ListByProjectAsync(Guid projectId, string? statusFilter, CancellationToken ct = default);
    Task<Plan> CreateAsync(
        Guid projectId, string planTypeId, string name, string? objective,
        Guid? createdByActorId, Guid? primaryBoardId, string? primarySlackChannelId,
        CancellationToken ct = default);
    Task<Plan?> SetStatusAsync(Guid id, string newStatus, CancellationToken ct = default);

    /// <summary>Record the GitHub milestone number created for this plan's audit run.</summary>
    Task SetMilestoneNumberAsync(Guid planId, int milestoneNumber, CancellationToken ct = default);

    Task<Guid> AddPhaseAsync(Guid planId, int orderIndex, string name, CancellationToken ct = default);
    Task<IReadOnlyList<Phase>> ListPhasesAsync(Guid planId, CancellationToken ct = default);

    Task<TaskCountsByStatus> GetTaskCountsAsync(Guid planId, CancellationToken ct = default);
}

public sealed record TaskCountsByStatus(IReadOnlyDictionary<string, int> Counts);
