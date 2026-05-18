using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Workstream.Core.StateMachine;

/// <summary>
/// Parses the JSONB string stored in <c>plan_types.state_graph</c> into an immutable
/// <see cref="StateGraph"/> instance. Tolerates missing finding sections (dev plans have none).
/// </summary>
public static class StateGraphParser
{
    public static StateGraph Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    public static StateGraph Parse(JsonElement root)
    {
        var taskStates       = ReadStringArray(root, "task_states");
        var taskInitial      = root.GetProperty("task_initial").GetString()
                                ?? throw new InvalidOperationException("state_graph.task_initial missing");
        var taskTerminal     = new HashSet<string>(ReadStringArray(root, "task_terminal"));
        var taskTransitions  = ReadTransitions(root, "task_transitions");

        var findingStates    = TryReadStringArray(root, "finding_states");
        string? findingInitial = null;
        if (root.TryGetProperty("finding_initial", out var fi) && fi.ValueKind != JsonValueKind.Null)
            findingInitial = fi.GetString();
        var findingTerminal  = new HashSet<string>(TryReadStringArray(root, "finding_terminal"));
        var findingTransitions = ReadTransitions(root, "finding_transitions");

        var boardMapping = ReadStringMap(root, "board_column_mapping");

        return new StateGraph(
            taskStates,
            taskInitial,
            taskTerminal,
            taskTransitions,
            findingStates,
            findingInitial,
            findingTerminal,
            findingTransitions,
            boardMapping);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"state_graph.{prop} missing or not array");
        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            var s = item.GetString();
            if (s is not null) list.Add(s);
        }
        return list;
    }

    private static IReadOnlyList<string> TryReadStringArray(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            var s = item.GetString();
            if (s is not null) list.Add(s);
        }
        return list;
    }

    private static IReadOnlyList<TransitionRule> ReadTransitions(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<TransitionRule>();

        var list = new List<TransitionRule>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            string from  = item.GetProperty("from").GetString() ?? throw new InvalidOperationException("transition.from missing");
            string to    = item.GetProperty("to").GetString()   ?? throw new InvalidOperationException("transition.to missing");
            string via   = item.GetProperty("via").GetString()  ?? throw new InvalidOperationException("transition.via missing");

            string? verdict = null;
            if (item.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String)
                verdict = v.GetString();

            IReadOnlyList<string>? requiresRole = null;
            if (item.TryGetProperty("requires_role", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                var roles = new List<string>(r.GetArrayLength());
                foreach (var roleEl in r.EnumerateArray())
                {
                    var rs = roleEl.GetString();
                    if (rs is not null) roles.Add(rs);
                }
                requiresRole = roles;
            }

            string? requiresPermission = null;
            if (item.TryGetProperty("requires_permission", out var rp) && rp.ValueKind == JsonValueKind.String)
                requiresPermission = rp.GetString();

            string? guard = null;
            if (item.TryGetProperty("guard", out var g) && g.ValueKind == JsonValueKind.String)
                guard = g.GetString();

            list.Add(new TransitionRule(from, to, via, verdict, requiresRole, requiresPermission, guard));
        }
        return list;
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();
        var map = new Dictionary<string, string>();
        foreach (var kv in el.EnumerateObject())
        {
            var s = kv.Value.GetString();
            if (s is not null) map[kv.Name] = s;
        }
        return map;
    }
}
