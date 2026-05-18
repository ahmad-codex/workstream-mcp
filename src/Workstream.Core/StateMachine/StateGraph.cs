using System.Collections.Generic;

namespace Workstream.Core.StateMachine;

/// <summary>
/// One legal transition rule. Mirrors the JSONB shape in §5.1: <c>{ "from", "to", "via", ... }</c>.
/// <see cref="From"/> may be the literal <c>"*"</c> wildcard.
/// </summary>
public sealed record TransitionRule(
    string From,
    string To,
    string Via,
    string? Verdict,
    IReadOnlyList<string>? RequiresRole,
    string? RequiresPermission,
    string? Guard);

/// <summary>
/// Parsed representation of <c>plan_types.state_graph</c>. Immutable; cache-friendly.
/// </summary>
public sealed record StateGraph(
    IReadOnlyList<string>                TaskStates,
    string                               TaskInitial,
    IReadOnlySet<string>                 TaskTerminal,
    IReadOnlyList<TransitionRule>        TaskTransitions,
    IReadOnlyList<string>                FindingStates,
    string?                              FindingInitial,
    IReadOnlySet<string>                 FindingTerminal,
    IReadOnlyList<TransitionRule>        FindingTransitions,
    IReadOnlyDictionary<string, string>  BoardColumnMapping)
{
    public IReadOnlyList<TransitionRule> TransitionsFor(string entityType) => entityType switch
    {
        Domain.EntityType.Task    => TaskTransitions,
        Domain.EntityType.Finding => FindingTransitions,
        _                         => System.Array.Empty<TransitionRule>(),
    };

    public IReadOnlyList<string> StatesFor(string entityType) => entityType switch
    {
        Domain.EntityType.Task    => TaskStates,
        Domain.EntityType.Finding => FindingStates,
        _                         => System.Array.Empty<string>(),
    };

    public IReadOnlySet<string> TerminalFor(string entityType) => entityType switch
    {
        Domain.EntityType.Task    => TaskTerminal,
        Domain.EntityType.Finding => FindingTerminal,
        _                         => new HashSet<string>(),
    };
}
