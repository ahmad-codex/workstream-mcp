using System;

namespace Workstream.Core.Domain;

public sealed record Plan
{
    public Guid    Id                     { get; init; }
    public Guid    ProjectId              { get; init; }
    public string  PlanTypeId             { get; init; } = "";
    public string  Name                   { get; init; } = "";
    public string? Objective              { get; init; }
    public string  Status                 { get; init; } = "draft";
    public Guid?   CreatedByActorId       { get; init; }
    public Guid?   PrimaryBoardId         { get; init; }
    public string? PrimarySlackChannelId  { get; init; }
    public string  Config                 { get; init; } = "{}";
    public DateTimeOffset  CreatedAt      { get; init; }
    public DateTimeOffset? ActivatedAt    { get; init; }
    public DateTimeOffset? CompletedAt    { get; init; }
}

public sealed record Phase
{
    public Guid   Id         { get; init; }
    public Guid   PlanId     { get; init; }
    public int    OrderIndex { get; init; }
    public string Name       { get; init; } = "";
    public string Status     { get; init; } = "pending";
    public DateTimeOffset CreatedAt { get; init; }
}
