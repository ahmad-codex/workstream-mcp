namespace Workstream.Core.Domain;

/// <summary>
/// String constants identifying the type column written to the <c>events</c> table and used
/// throughout the state machine to dispatch task vs finding vs attempt transitions.
/// </summary>
public static class EntityType
{
    public const string Task     = "task";
    public const string Finding  = "finding";
    public const string Attempt  = "attempt";
    public const string Plan     = "plan";
    public const string Phase    = "phase";
    public const string Verdict  = "verdict";
}

public static class ActorType
{
    public const string Human        = "human";
    public const string Orchestrator = "orchestrator";
    public const string Subagent     = "subagent";
}

public static class TaskStatus
{
    public const string Pending          = "pending";
    public const string Claimed          = "claimed";
    public const string InProgress       = "in_progress";
    public const string Review           = "review";
    public const string Done             = "done";
    public const string Deferred         = "deferred";
    public const string Blocked          = "blocked";
    public const string NeedsHumanReview = "needs_human_review";
    public const string Skipped          = "skipped";
    public const string OutOfScope       = "out_of_scope";
}

public static class FindingStatus
{
    public const string PendingVerification = "pending_verification";
    public const string Confirmed           = "confirmed";
    public const string Rejected            = "rejected";
    public const string Ambiguous           = "ambiguous";
    public const string InFix               = "in_fix";
    public const string Fixed               = "fixed";
    public const string FixFailed           = "fix_failed";
    public const string Partial             = "partial";
    public const string NeedsHumanReview    = "needs_human_review";
    public const string Deferred            = "deferred";
}

public static class PlanStatus
{
    public const string Draft     = "draft";
    public const string Active    = "active";
    public const string Paused    = "paused";
    public const string Completed = "completed";
    public const string Archived  = "archived";
}

public static class VerdictType
{
    public const string Confirmed        = "confirmed";
    public const string Rejected         = "rejected";
    public const string Ambiguous        = "ambiguous";
    public const string FixConfirmed     = "fix_confirmed";
    public const string FixFailed        = "fix_failed";
    public const string Partial          = "partial";
    public const string Approved         = "approved";
    public const string ChangesRequested = "changes_requested";
}
