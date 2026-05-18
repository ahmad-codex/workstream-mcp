using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.StateMachine;
using Workstream.Data.Repositories;

namespace Workstream.Data;

/// <summary>
/// Production <see cref="IPlanTypeCache"/>: lazy-load from Postgres on first hit, cache the
/// parsed <see cref="StateGraph"/>. Invalidation is driven by a LISTEN/NOTIFY worker that
/// fires <see cref="Invalidate"/> when a row in <c>plan_types</c> is updated.
/// </summary>
public sealed class PostgresPlanTypeCache : IPlanTypeCache
{
    private readonly IPlanTypeRepository _repo;
    private readonly ConcurrentDictionary<string, (PlanType Row, StateGraph Graph)> _entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadLocks = new();

    public PostgresPlanTypeCache(IPlanTypeRepository repo) => _repo = repo;

    public async Task<(PlanType Row, StateGraph Graph)?> GetAsync(string planTypeId, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(planTypeId, out var hit))
            return hit;

        var gate = _loadLocks.GetOrAdd(planTypeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(planTypeId, out hit)) return hit;
            var row = await _repo.GetAsync(planTypeId, ct).ConfigureAwait(false);
            if (row is null) return null;
            var graph = StateGraphParser.Parse(row.StateGraphJson);
            _entries[planTypeId] = (row, graph);
            return (row, graph);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(string planTypeId) => _entries.TryRemove(planTypeId, out _);

    public void Clear() => _entries.Clear();
}
