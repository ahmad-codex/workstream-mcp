using System;

namespace Workstream.Core.Domain;

public sealed record Organization(
    Guid           Id,
    string         Name,
    DateTimeOffset CreatedAt);
