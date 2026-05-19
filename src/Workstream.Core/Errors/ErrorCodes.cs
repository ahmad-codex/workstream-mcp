namespace Workstream.Core.Errors;

/// <summary>
/// Stable error codes (§9.7). Returned in <c>{ ok: false, error: { code, message, details } }</c>.
/// These strings are part of the public MCP contract — orchestrator LLMs branch on them.
/// </summary>
public static class ErrorCodes
{
    public const string Unauthorized            = "unauthorized";
    public const string NotFound                = "not_found";
    public const string StaleClaim              = "stale_claim";
    public const string IllegalTransition       = "illegal_transition";
    public const string PermissionDenied        = "permission_denied";
    public const string RetryCapExceeded        = "retry_cap_exceeded";
    public const string ValidationError         = "validation_error";
    public const string Conflict                = "conflict";
    public const string ExternalDependencyError = "external_dependency_error";
    public const string ServiceUnavailable      = "service_unavailable";
    public const string TaskUnavailable         = "task_unavailable";
    public const string NoWorkAvailable         = "no_work_available";
    public const string RoleNotAllowed          = "role_not_allowed";
    public const string PlanTypeUnknown         = "plan_type_unknown";
    public const string GuardFailed             = "guard_failed";
    public const string DependenciesUnmet       = "dependencies_unmet";
    public const string DependencyCycle         = "dependency_cycle";
}
