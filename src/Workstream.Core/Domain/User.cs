using System;

namespace Workstream.Core.Domain;

public sealed record User(
    Guid    Id,
    string  GithubUsername,
    string? DisplayName,
    string  ActorType,
    bool    IsActive,
    bool    CanOverrideVerdict,
    bool    CanArchivePlan,
    bool    CanMarkNeedsHumanReview,
    bool    IsAdmin,
    string  Config,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);
