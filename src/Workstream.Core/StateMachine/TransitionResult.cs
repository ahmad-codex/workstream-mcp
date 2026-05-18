using System.Collections.Generic;

namespace Workstream.Core.StateMachine;

/// <summary>
/// Outcome of <see cref="StateMachineService.ValidateTransition"/>. On rejection,
/// <see cref="AllowedNext"/> is populated so the caller (often an LLM) can self-correct.
/// </summary>
public sealed record TransitionResult(
    bool                    Allowed,
    string?                 RejectionCode,
    string?                 RejectionMessage,
    IReadOnlyList<string>?  AllowedNext,
    string?                 ViolatedGuard,
    string?                 RequiredPermission,
    IReadOnlyList<string>?  RequiredRoles)
{
    public static TransitionResult Ok() => new(true, null, null, null, null, null, null);

    public static TransitionResult Illegal(string from, string to, IReadOnlyList<string> allowedNext) =>
        new(false,
            Errors.ErrorCodes.IllegalTransition,
            $"transition {from} -> {to} is not legal",
            allowedNext,
            null, null, null);

    public static TransitionResult RoleNotAllowed(IReadOnlyList<string> roles) =>
        new(false,
            Errors.ErrorCodes.RoleNotAllowed,
            $"this transition requires one of roles: {string.Join(", ", roles)}",
            null, null, null, roles);

    public static TransitionResult PermissionDenied(string permission) =>
        new(false,
            Errors.ErrorCodes.PermissionDenied,
            $"permission '{permission}' required",
            null, null, permission, null);

    public static TransitionResult GuardFailed(string guard) =>
        new(false,
            Errors.ErrorCodes.GuardFailed,
            $"guard '{guard}' did not hold",
            null, guard, null, null);
}
