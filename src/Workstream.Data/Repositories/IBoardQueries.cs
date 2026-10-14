using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Workstream.Data.Repositories;

/// <summary>
/// Read-only queries for the web dashboard. Claim tokens are never selected: the dashboard
/// shows who holds a claim and until when, not the token that proves it.
/// </summary>
public interface IBoardQueries
{
    Task<IReadOnlyList<BoardTask>> ListTasksAsync(Guid planId, CancellationToken ct = default);
    Task<IReadOnlyList<BoardFinding>> ListFindingsAsync(Guid planId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, string>> GetActorNamesAsync(IEnumerable<Guid> actorIds, CancellationToken ct = default);
}

public sealed record BoardTask
{
    public Guid            Id              { get; init; }
    public string          ExternalKey     { get; init; } = "";
    public string          Title           { get; init; } = "";
    public string          Status          { get; init; } = "";
    public int             Priority        { get; init; }
    public string?         Phase           { get; init; }
    public Guid?           ClaimActorId    { get; init; }
    public string?         ClaimActor      { get; init; }
    public string?         ClaimRole       { get; init; }
    public DateTimeOffset? ClaimedAt       { get; init; }
    public DateTimeOffset? ClaimedUntil    { get; init; }
    public Guid[]          DependsOn       { get; init; } = Array.Empty<Guid>();
    public int?            GithubIssue     { get; init; }
    public DateTimeOffset  UpdatedAt       { get; init; }
}

public sealed record BoardFinding
{
    public Guid            Id              { get; init; }
    public Guid            TaskId          { get; init; }
    public string          ExternalKey     { get; init; } = "";
    public string?         Severity        { get; init; }
    public string?         Symptom         { get; init; }
    public string          Status          { get; init; } = "";
    public string?         ClaimActor      { get; init; }
    public string?         ClaimRole       { get; init; }
    public DateTimeOffset? ClaimedUntil    { get; init; }
    public DateTimeOffset  UpdatedAt       { get; init; }
}
