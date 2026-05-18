using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IAttemptRepository
{
    Task<Attempt?> GetAsync(Guid id, CancellationToken ct = default);
    Task<int> CountForFindingAsync(Guid findingId, CancellationToken ct = default);
    Task<int> CountForTaskAsync(Guid taskId, CancellationToken ct = default);
    Task<IReadOnlyList<Attempt>> ListForFindingAsync(Guid findingId, CancellationToken ct = default);
    Task<IReadOnlyList<Attempt>> ListForTaskAsync(Guid taskId, CancellationToken ct = default);

    Task<Attempt> InsertForFindingAsync(Guid findingId, Guid actorId, AttemptInput input, CancellationToken ct = default);
    Task<Attempt> InsertForTaskAsync(Guid taskId, Guid actorId, AttemptInput input, CancellationToken ct = default);

    Task<Attempt?> SetCommitHashAsync(Guid attemptId, Guid actorId, string commitHash, CancellationToken ct = default);
}
