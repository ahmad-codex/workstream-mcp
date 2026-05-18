using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IVerdictRepository
{
    Task<Verdict> InsertForFindingAsync(Guid findingId, Guid actorId, string verdictType, VerdictEvidence ev, CancellationToken ct = default);
    Task<Verdict> InsertForAttemptAsync(Guid attemptId, Guid actorId, string verdictType, VerdictEvidence ev, CancellationToken ct = default);
    Task<IReadOnlyList<Verdict>> ListForFindingAsync(Guid findingId, CancellationToken ct = default);
    Task<IReadOnlyList<Verdict>> ListForAttemptAsync(Guid attemptId, CancellationToken ct = default);
}
