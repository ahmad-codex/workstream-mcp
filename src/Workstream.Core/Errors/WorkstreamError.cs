using System;
using System.Collections.Generic;

namespace Workstream.Core.Errors;

/// <summary>
/// Structured error returned from MCP tools and from internal services. The <see cref="Details"/>
/// dictionary is intentionally untyped — its shape depends on <see cref="Code"/>. For example,
/// <c>illegal_transition</c> always populates <c>allowed_next: string[]</c> (§9.7).
/// </summary>
public sealed record WorkstreamError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?>? Details = null)
{
    public static WorkstreamError NotFound(string what)
        => new(ErrorCodes.NotFound, $"{what} not found");

    public static WorkstreamError Validation(string message, IReadOnlyDictionary<string, object?>? details = null)
        => new(ErrorCodes.ValidationError, message, details);

    public static WorkstreamError IllegalTransition(string from, string to, IReadOnlyList<string> allowedNext)
        => new(
            ErrorCodes.IllegalTransition,
            $"transition {from} -> {to} is not legal",
            new Dictionary<string, object?>
            {
                ["from"] = from,
                ["to"] = to,
                ["allowed_next"] = allowedNext,
            });

    public static WorkstreamError StaleClaim()
        => new(ErrorCodes.StaleClaim, "the claim token is expired or no longer matches the entity");

    public static WorkstreamError PermissionDenied(string permission)
        => new(ErrorCodes.PermissionDenied, $"permission '{permission}' required",
            new Dictionary<string, object?> { ["required_permission"] = permission });

    public static WorkstreamError PlanArchived(Guid planId)
        => new(
            ErrorCodes.PlanArchived,
            "this plan is archived (disabled) — archived plans accept no new claims or tasks; " +
            "create and activate a new plan to continue work",
            new Dictionary<string, object?>
            {
                ["plan_id"] = planId,
                ["status"]  = "archived",
            });
}
