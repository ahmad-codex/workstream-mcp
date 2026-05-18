namespace Workstream.Core.StateMachine;

/// <summary>
/// Extra inputs the state machine needs to evaluate guards. Callers populate the relevant
/// fields; unused fields default to null and the corresponding guards return false (which
/// is correct: a missing input means the precondition is not known to hold).
/// </summary>
public sealed record TransitionContext(
    int?  ProposedFindingCount    = null,    // submit_findings: number of findings being submitted
    int?  AttemptCount            = null,    // submit_attempt / retry decisions
    int?  RetryCap                = null,    // pulled from plan_types.retry_cap
    bool? AllFindingsTerminal     = null,    // for the rollup transition (review → done)
    bool? AllFindingsResolvedOk   = null,    // for "no rejected/needs_human_review left"
    string? Verdict               = null)    // overrides graph rule verdict matching
{
    public static TransitionContext Empty { get; } = new();
}
