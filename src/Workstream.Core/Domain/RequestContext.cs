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
    DateTimeOffset Now)
{
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
