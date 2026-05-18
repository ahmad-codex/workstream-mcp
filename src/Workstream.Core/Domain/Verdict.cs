using System;

namespace Workstream.Core.Domain;

public sealed record Verdict(
    Guid    Id,
    Guid?   FindingId,
    Guid?   AttemptId,
    string  VerdictType,
    string? ReasonCategory,
    string? PreOutput,
    string? PostOutput,
    string? AdversarialOutput,
    string? InvariantEvidence,    // JSONB serialized
    Guid?   ActorId,
    DateTimeOffset At);

/// <summary>
/// Verification evidence package supplied by a verifier or fix-verifier (§9.3).
/// </summary>
public sealed record VerdictEvidence(
    string? PreOutput,
    string? PostOutput,
    string? AdversarialOutput,
    string? InvariantEvidence,    // raw JSON string
    string? ReasonCategory);
