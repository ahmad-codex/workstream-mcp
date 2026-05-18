using System;

namespace Workstream.Core.Domain;

public sealed record Organization
{
    public Guid           Id        { get; init; }
    public string         Name      { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}
