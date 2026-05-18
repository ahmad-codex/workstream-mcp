using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Core.StateMachine;

/// <summary>
/// Test/local implementation of <see cref="IPlanTypeCache"/>. Production code uses the
/// Postgres-backed implementation in <c>Workstream.Data</c>.
/// </summary>
public sealed class InMemoryPlanTypeCache : IPlanTypeCache
{
    private readonly ConcurrentDictionary<string, (PlanType Row, StateGraph Graph)> _entries = new();

    public Task<(PlanType Row, StateGraph Graph)?> GetAsync(string planTypeId, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(planTypeId, out var hit))
            return Task.FromResult<(PlanType, StateGraph)?>(hit);
        return Task.FromResult<(PlanType, StateGraph)?>(null);
    }

    public void Invalidate(string planTypeId) => _entries.TryRemove(planTypeId, out _);

    public void Set(PlanType row)
    {
        var graph = StateGraphParser.Parse(row.StateGraphJson);
        _entries[row.Id] = (row, graph);
    }
}
