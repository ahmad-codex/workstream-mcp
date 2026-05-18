using System;

namespace Workstream.Core.Domain;

public sealed record Project(
    Guid    Id,
    Guid?   OrganizationId,
    string  Slug,
    string  DisplayName,
    string? Description,
    string  Config,
    DateTimeOffset CreatedAt);

public sealed record ProjectRepo(
    Guid    Id,
    Guid    ProjectId,
    string  GithubOwner,
    string  GithubRepo,
    string  DefaultBranch,
    bool    IsReferenceOnly,
    DateTimeOffset CreatedAt);

public sealed record ProjectBoard(
    Guid    Id,
    Guid    ProjectId,
    string  GithubProjectV2NodeId,
    int     GithubProjectNumber,
    string  GithubOwner,
    string  DisplayName,
    string  StatusFieldNodeId,
    string  StatusOptionBacklog,
    string  StatusOptionInProgress,
    string  StatusOptionReview,
    string  StatusOptionDone,
    string? StatusOptionBlocked,
    string  Config,
    DateTimeOffset CreatedAt);

public sealed record ProjectSlack(
    Guid     ProjectId,
    string   WorkspaceId,
    string   BotTokenSecretRef,
    string   DefaultChannelId,
    string[] NotifyOn,
    DateTimeOffset CreatedAt);
