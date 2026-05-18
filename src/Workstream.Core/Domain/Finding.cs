using System;

namespace Workstream.Core.Domain;

public sealed record Finding(
    Guid     Id,
    Guid     TaskId,
    string   ExternalKey,
    string?  Severity,
    string?  InvariantImpact,
    string?  Symptom,
    string?  RootCause,
    string?  ReproSteps,
    string?  AdversarialInput,
    string?  Expected,
    string?  Actual,
    string?  ReferenceComparison,
    string   Status,
    ClaimState Claim,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Input shape for <c>submit_findings</c>. The auditor populates one of these per finding;
/// the server validates and creates a <see cref="Finding"/> row per item.
/// </summary>
public sealed record FindingInput(
    string  ExternalKey,
    string? Severity,
    string? InvariantImpact,
    string? Symptom,
    string? RootCause,
    string? ReproSteps,
    string? AdversarialInput,
    string? Expected,
    string? Actual,
    string? ReferenceComparison);
