using System;

namespace Workstream.Core.Domain;

// Property-init style (not positional) so Dapper uses the parameterless-ctor + property
// path. See User.cs for the rationale.
public sealed record Project
{
    public Guid    Id             { get; init; }
    public Guid?   OrganizationId { get; init; }
    public string  Slug           { get; init; } = "";
    public string  DisplayName    { get; init; } = "";
    public string? Description    { get; init; }
    public string  Config         { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record ProjectRepo
{
    public Guid    Id              { get; init; }
    public Guid    ProjectId       { get; init; }
    public string  GithubOwner     { get; init; } = "";
    public string  GithubRepo      { get; init; } = "";
    public string  DefaultBranch   { get; init; } = "main";
    public bool    IsReferenceOnly { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record ProjectBoard
{
    public Guid    Id                       { get; init; }
    public Guid    ProjectId                { get; init; }
    public string  GithubProjectV2NodeId    { get; init; } = "";
    public int     GithubProjectNumber      { get; init; }
    public string  GithubOwner              { get; init; } = "";
    public string  DisplayName              { get; init; } = "";
    public string  StatusFieldNodeId        { get; init; } = "";
    public string  StatusOptionBacklog      { get; init; } = "";
    public string  StatusOptionInProgress   { get; init; } = "";
    public string  StatusOptionReview       { get; init; } = "";
    public string  StatusOptionDone         { get; init; } = "";
    public string? StatusOptionBlocked      { get; init; }
    public string  Config                   { get; init; } = "{}";
    public DateTimeOffset CreatedAt         { get; init; }
}

public sealed record ProjectSlack
{
    public Guid     ProjectId          { get; init; }
    public string   WorkspaceId        { get; init; } = "";
    public string   BotTokenSecretRef  { get; init; } = "";
    public string   DefaultChannelId   { get; init; } = "";
    public string[] NotifyOn           { get; init; } = Array.Empty<string>();
    public DateTimeOffset CreatedAt    { get; init; }
}
