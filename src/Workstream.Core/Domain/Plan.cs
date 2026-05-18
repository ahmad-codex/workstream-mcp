using System;

namespace Workstream.Core.Domain;

public sealed record Plan(
    Guid    Id,
    Guid    ProjectId,
    string  PlanTypeId,
    string  Name,
    string? Objective,
    string  Status,
    Guid?   CreatedByActorId,
    Guid?   PrimaryBoardId,
    string? PrimarySlackChannelId,
    string  Config,
    DateTimeOffset  CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? CompletedAt);

public sealed record Phase(
    Guid   Id,
    Guid   PlanId,
    int    OrderIndex,
    string Name,
    string Status,
    DateTimeOffset CreatedAt);
