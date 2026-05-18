using System;

namespace Workstream.Core.Domain;

// Property-init style (not positional) so Dapper uses the parameterless-ctor + property
// path. The positional path requires constructor-signature-exact column types, which
// Npgsql doesn't surface for `string?` (returns `string`) or timestamptz (returns DateTime).
public sealed record User
{
    public Guid    Id                       { get; init; }
    public string  GithubUsername           { get; init; } = "";
    public string? DisplayName              { get; init; }
    public string  ActorType                { get; init; } = "human";
    public bool    IsActive                 { get; init; } = true;
    public bool    CanOverrideVerdict       { get; init; }
    public bool    CanArchivePlan           { get; init; }
    public bool    CanMarkNeedsHumanReview  { get; init; } = true;
    public bool    IsAdmin                  { get; init; }
    public string  Config                   { get; init; } = "{}";
    public DateTimeOffset  CreatedAt        { get; init; }
    public DateTimeOffset? LastSeenAt       { get; init; }
}
