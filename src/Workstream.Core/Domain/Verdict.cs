using System;

namespace Workstream.Core.Domain;

public sealed record Verdict
{
    public Guid    Id                { get; init; }
    public Guid?   FindingId         { get; init; }
    public Guid?   AttemptId         { get; init; }
    public string  VerdictType       { get; init; } = "";
    public string? ReasonCategory    { get; init; }
    public string? PreOutput         { get; init; }
    public string? PostOutput        { get; init; }
    public string? AdversarialOutput { get; init; }
    public string? InvariantEvidence { get; init; }    // JSONB serialized
    public Guid?   ActorId           { get; init; }
    public DateTimeOffset At         { get; init; }
}

/// <summary>
/// Verification evidence package supplied by a verifier or fix-verifier (§9.3).
/// Input shape — not hydrated by Dapper, so positional ctor stays.
/// </summary>
public sealed record VerdictEvidence(
    string? PreOutput,
    string? PostOutput,
    string? AdversarialOutput,
    string? InvariantEvidence,    // raw JSON string
    string? ReasonCategory);
