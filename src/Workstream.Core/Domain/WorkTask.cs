using System;

namespace Workstream.Core.Domain;

/// <summary>
/// The atomic work unit. Named <c>WorkTask</c> to avoid collision with
/// <see cref="System.Threading.Tasks.Task"/>; the database table is still <c>tasks</c>.
/// </summary>
public sealed record WorkTask(
    Guid     Id,
    Guid     PlanId,
    Guid?    PhaseId,
    string   ExternalKey,
    string   Title,
    string?  Description,
    string[]? Paths,
    string?  ReferencePointer,
    int      Priority,
    string   Status,
    Guid?    AssigneeActorId,
    string?  GithubBoardItemId,
    int?     GithubIssueNumber,
    string?  GithubIssueNodeId,
    ClaimState Claim,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
