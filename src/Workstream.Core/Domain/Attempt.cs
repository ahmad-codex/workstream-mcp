using System;

namespace Workstream.Core.Domain;

public sealed record Attempt(
    Guid      Id,
    Guid?     FindingId,
    Guid?     TaskId,
    int       AttemptNumber,
    string[]? FilesChanged,
    string?   ApproachSummary,
    string?   SideEffects,
    string?   BuildCommand,
    string?   TestScenario,
    string?   DiffRef,
    string?   CommitHash,
    Guid?     ActorId,
    ClaimState Claim,
    DateTimeOffset CreatedAt);

/// <summary>
/// Submission payload for <c>submit_attempt</c>. The server assigns <c>AttemptNumber</c>
/// monotonically per parent entity (§9.3).
/// </summary>
public sealed record AttemptInput(
    string[]? FilesChanged,
    string?   ApproachSummary,
    string?   SideEffects,
    string?   BuildCommand,
    string?   TestScenario,
    string?   DiffRef);
