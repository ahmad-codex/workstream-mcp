using Workstream.Core.Domain;
using Workstream.Core.Errors;

namespace Workstream.Mcp.Tools;

/// <summary>
/// Cross-cutting plan-state guard shared by the work-entry tools (claims, task creation,
/// activation). A plan in the <c>archived</c> status is frozen: it accepts no new claims,
/// no new tasks, and cannot be re-activated. An orchestrator that hits
/// <see cref="EnsureNotArchived"/> should treat the plan as gone and create a fresh one —
/// archiving a plan is precisely how an operator forces the next bootstrap to start clean.
/// </summary>
internal static class PlanGuards
{
    /// <summary>
    /// Throws the structured <c>plan_archived</c> error when the plan has been archived.
    /// The error carries the plan id so the calling orchestrator can branch on it.
    /// </summary>
    public static void EnsureNotArchived(Plan plan)
    {
        if (plan.Status == PlanStatus.Archived)
            throw new WorkstreamException(WorkstreamError.PlanArchived(plan.Id));
    }
}
