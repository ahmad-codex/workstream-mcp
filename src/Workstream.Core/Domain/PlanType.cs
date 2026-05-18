using System;

namespace Workstream.Core.Domain;

/// <summary>
/// Configuration row for one plan type (e.g. <c>audit</c>, <c>development</c>).
/// The <see cref="StateGraphJson"/>, <see cref="RoleTtlsJson"/>, and
/// <see cref="BoardColumnMappingJson"/> are raw JSON strings stored in JSONB columns;
/// the <c>Workstream.Core.StateMachine</c> services parse them on load and cache the result.
/// </summary>
public sealed record PlanType(
    string  Id,
    string  DisplayName,
    string  StateGraphJson,
    string  RoleTtlsJson,
    int     RetryCap,
    string? PromptTemplateRef,
    bool    RequiresFindings,
    string  BoardColumnMappingJson,
    string  ConfigJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
