using System;

namespace Workstream.Core.Domain;

/// <summary>
/// Claim columns common to tasks, findings, and attempts. All-or-nothing: either every
/// field is set (a claim is active) or every field is null (the row is unclaimed).
/// Enforced by check constraints on each table (§4.5).
/// </summary>
public readonly record struct ClaimState(
    Guid?   ActorId,
    string? Role,
    Guid?   Token,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? ClaimedUntil)
{
    public bool IsActive => Token is not null;

    public bool IsExpired(DateTimeOffset now)
        => IsActive && ClaimedUntil is { } until && until < now;

    public static ClaimState Unclaimed => default;
}
