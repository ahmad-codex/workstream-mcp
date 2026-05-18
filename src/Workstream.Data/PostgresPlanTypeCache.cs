using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
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

            // The schema (§4.2) stores board_column_mapping in a separate jsonb column on
            // plan_types — distinct from the embedded state_graph.board_column_mapping the
            // spec example shows. The seed migration 0002 wrote to the separate column. If
            // the parsed state_graph has no mapping but the row column does, splice it in.
            if (graph.BoardColumnMapping.Count == 0 && !string.IsNullOrWhiteSpace(row.BoardColumnMappingJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(row.BoardColumnMappingJson);
                    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } v)
                            dict[prop.Name] = v;
                    }
                    graph = graph with { BoardColumnMapping = dict };
                }
                catch (JsonException) { /* leave the empty mapping in place */ }
            }

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
