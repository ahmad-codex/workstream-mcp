using System;

namespace Workstream.Core.Domain;

/// <summary>
/// Configuration row for one plan type (e.g. <c>audit</c>, <c>development</c>).
/// The JSON-bearing properties are raw JSON strings stored in JSONB columns; the
/// <c>Workstream.Core.StateMachine</c> services parse them on load and cache the result.
/// </summary>
public sealed record PlanType
{
    public string  Id                     { get; init; } = "";
    public string  DisplayName            { get; init; } = "";
    public string  StateGraphJson         { get; init; } = "{}";
    public string  RoleTtlsJson           { get; init; } = "{}";
    public int     RetryCap               { get; init; } = 3;
    public string? PromptTemplateRef      { get; init; }
    public bool    RequiresFindings       { get; init; }
    public string  BoardColumnMappingJson { get; init; } = "{}";
    public string  ConfigJson             { get; init; } = "{}";
    public DateTimeOffset CreatedAt       { get; init; }
    public DateTimeOffset UpdatedAt       { get; init; }
}
