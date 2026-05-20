using System;

namespace Workstream.Core.Domain;

/// <summary>
/// Per-request ambient context. Populated by the URL-token resolution middleware (§3.2)
/// and consumed by every MCP tool. Carries the resolved actor and traceability fields.
/// </summary>
public sealed record RequestContext(
    Guid    ActorId,
    string  ActorType,
    string  GithubUsername,
    bool    IsAdmin,
    bool    CanOverrideVerdict,
    bool    CanArchivePlan,
    bool    CanMarkNeedsHumanReview,
    string  TraceId,
    DateTimeOffset Now,
    string? DisplayName = null,
    string? ActingRole = null)
{
    // ActingRole: optional agent role declared per-call via the `as_agent` payload
    // field (e.g. "auditor", "verifier", "fixer", "fix_verifier"). When set, Slack
    // posts render that role's persona even though the token belongs to a human —
    // attribution follows the declared role. Display-only: the events table still
    // records the real ActorId.

    /// <summary>
    /// User-facing label for templates (Slack body, etc.) — display name when present,
    /// github username otherwise. Identity/audit code paths should use
    /// <see cref="GithubUsername"/> directly so logs and event payloads stay stable.
    /// </summary>
    public string DisplayActor => string.IsNullOrWhiteSpace(DisplayName) ? GithubUsername : DisplayName!;

    public bool HasPermission(string permission) => permission switch
    {
        "can_override_verdict"        => CanOverrideVerdict,
        "can_archive_plan"            => CanArchivePlan,
        "can_mark_needs_human_review" => CanMarkNeedsHumanReview,
        "is_admin"                    => IsAdmin,
        _ => false
    };
}

/// <summary>
/// Holder allowing services to read the current request's <see cref="RequestContext"/>
/// without dragging an explicit argument through every call site. Implemented by an
/// AsyncLocal-backed accessor in <c>Workstream.Api</c>.
/// </summary>
public interface IRequestContextAccessor
{
    RequestContext? Current { get; }
}
