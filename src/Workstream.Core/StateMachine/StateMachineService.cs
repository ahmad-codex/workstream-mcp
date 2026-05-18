using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Core.Errors;

namespace Workstream.Core.StateMachine;

/// <summary>
/// Single point of state-transition truth (§5.3). Every mutating MCP tool routes through
/// <see cref="ValidateTransitionAsync"/> before applying a status change.
///
/// The service is pure: it does not mutate state, does not touch the database. Its inputs
/// are <c>(planTypeId, entityType, from, to, via, role, context)</c>; its output is a
/// <see cref="TransitionResult"/>. Side-effect-free validation makes it trivial to unit-test
/// exhaustively against both seed profiles.
/// </summary>
public sealed class StateMachineService
{
    private readonly IPlanTypeCache _planTypes;

    public StateMachineService(IPlanTypeCache planTypes) => _planTypes = planTypes;

    /// <summary>Synchronous overload used inside tools that have already fetched the graph.</summary>
    public TransitionResult ValidateTransition(
        StateGraph graph,
        string entityType,
        string from,
        string to,
        string via,
        RequestContext actor,
        string? claimRole,
        TransitionContext context)
    {
        var transitions = graph.TransitionsFor(entityType);
        if (transitions.Count == 0)
        {
            return TransitionResult.Illegal(from, to, Array.Empty<string>());
        }

        // Find candidate rules: from matches (with wildcard) AND to matches AND via matches.
        // Then verdict matches if both rule and context specify one.
        var candidates = transitions.Where(r =>
                (r.From == from || r.From == "*")
                && r.To == to
                && r.Via == via
                && VerdictMatches(r.Verdict, context.Verdict))
            .ToList();

        if (candidates.Count == 0)
        {
            var allowedNext = AllowedNext(transitions, from);
            return TransitionResult.Illegal(from, to, allowedNext);
        }

        // Pick the most-specific candidate: prefer rules with both a guard and a role,
        // then a guard, then a role, then unguarded. Within a tier we just take the first
        // (the seed profiles never define ambiguous overlapping rules in the same tier).
        var ordered = candidates
            .OrderByDescending(r => (r.Guard is not null ? 2 : 0) + (r.RequiresRole is not null ? 1 : 0))
            .ToList();

        foreach (var rule in ordered)
        {
            // Role check
            if (rule.RequiresRole is { Count: > 0 } reqRoles)
            {
                if (claimRole is null || !reqRoles.Contains(claimRole))
                {
                    // Try the next candidate in case a less-restrictive one matches; if none match,
                    // we'll fall through with the most-restrictive rejection.
                    continue;
                }
            }

            // Permission check
            if (rule.RequiresPermission is { } perm && !actor.HasPermission(perm))
            {
                return TransitionResult.PermissionDenied(perm);
            }

            // Guard check
            if (rule.Guard is { } guard)
            {
                var (passed, reason) = EvaluateGuard(guard, context);
                if (!passed)
                {
                    return TransitionResult.GuardFailed(reason ?? guard);
                }
            }

            return TransitionResult.Ok();
        }

        // No candidate's role list was satisfied. Surface the role requirement from the
        // most-restrictive rule so the caller knows what roles would have worked.
        var requiredRoles = ordered.SelectMany(r => r.RequiresRole ?? Array.Empty<string>())
                                   .Distinct()
                                   .ToList();
        if (requiredRoles.Count > 0)
            return TransitionResult.RoleNotAllowed(requiredRoles);

        return TransitionResult.Illegal(from, to, AllowedNext(transitions, from));
    }

    public async Task<TransitionResult> ValidateTransitionAsync(
        string planTypeId,
        string entityType,
        string from,
        string to,
        string via,
        RequestContext actor,
        string? claimRole,
        TransitionContext context,
        CancellationToken ct = default)
    {
        var pt = await _planTypes.GetAsync(planTypeId, ct).ConfigureAwait(false);
        if (pt is null)
        {
            return new TransitionResult(
                false,
                ErrorCodes.PlanTypeUnknown,
                $"plan_type '{planTypeId}' not found",
                null, null, null, null);
        }
        return ValidateTransition(pt.Value.Graph, entityType, from, to, via, actor, claimRole, context);
    }

    private static bool VerdictMatches(string? ruleVerdict, string? contextVerdict)
    {
        if (ruleVerdict is null) return true;            // rule doesn't constrain verdict
        if (contextVerdict is null) return false;        // rule constrains but caller didn't supply
        return string.Equals(ruleVerdict, contextVerdict, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> AllowedNext(IReadOnlyList<TransitionRule> rules, string from)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rules)
            if (r.From == from || r.From == "*")
                set.Add(r.To);
        return set.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private static (bool Passed, string? Reason) EvaluateGuard(string guard, TransitionContext c)
    {
        return guard switch
        {
            "findings_empty"               => (c.ProposedFindingCount is 0, "findings list must be empty"),
            "findings_non_empty"           => (c.ProposedFindingCount is > 0, "findings list must contain at least one finding"),
            "below_retry_cap"              => (c.AttemptCount is { } n && c.RetryCap is { } cap && n < cap,
                                               "attempt count is at or above retry cap"),
            "at_retry_cap"                 => (c.AttemptCount is { } n2 && c.RetryCap is { } cap2 && n2 >= cap2,
                                               "attempt count is below retry cap"),
            "all_findings_terminal_resolved" => (c.AllFindingsTerminal is true && c.AllFindingsResolvedOk is true,
                                               "not every finding is in a terminal resolved state"),
            _ => (false, $"unknown guard '{guard}'"),
        };
    }

    /// <summary>
    /// Compute the board column key for a given internal status on a given plan type,
    /// applying the per-plan-override → per-board-explicit → per-plan-type-default precedence
    /// described in §7.4. The actual GitHub option-id resolution happens in <c>Workstream.GitHub</c>.
    /// </summary>
    public static string ResolveBoardColumn(
        StateGraph graph,
        string status,
        IReadOnlyDictionary<string, string>? planOverride = null)
    {
        if (planOverride is not null && planOverride.TryGetValue(status, out var po)) return po;
        if (graph.BoardColumnMapping.TryGetValue(status, out var def)) return def;
        return "backlog";
    }
}
