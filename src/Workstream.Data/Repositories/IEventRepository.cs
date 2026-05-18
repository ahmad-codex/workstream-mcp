using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IEventRepository
{
    Task EmitAsync(Guid actorId, string entityType, Guid entityId, string eventType, string? fromState, string? toState, string? payloadJson, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> ListForEntityAsync(string entityType, Guid entityId, DateTimeOffset? since, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Event>> ListRecentForPlanAsync(Guid planId, int limit, CancellationToken ct = default);
}
