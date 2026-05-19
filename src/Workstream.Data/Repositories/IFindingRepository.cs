using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IFindingRepository
{
    Task<Finding?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Look up a finding by its current claim token. Returns null if no row matches. Used
    /// by <c>refresh_claim</c> to dispatch to the right repository without the caller
    /// having to know which entity the token belongs to.
    /// </summary>
    Task<Finding?> GetByClaimTokenAsync(Guid claimToken, CancellationToken ct = default);

    Task<IReadOnlyList<Finding>> ListByTaskAsync(Guid taskId, CancellationToken ct = default);
    Task<IReadOnlyList<Finding>> ListByStatusAsync(Guid planId, IEnumerable<string> statuses, CancellationToken ct = default);

    Task<IReadOnlyList<Finding>> InsertManyAsync(
        Guid taskId,
        IReadOnlyList<FindingInput> inputs,
        CancellationToken ct = default);

    /// <summary>Claim the highest-severity pending-verification finding on the plan.</summary>
    Task<ClaimedFinding?> ClaimNextForVerificationAsync(
        Guid planId,
        Guid actorId,
        TimeSpan ttl,
        CancellationToken ct = default);

    /// <summary>Claim the highest-severity confirmed (or fix_failed, below cap) finding for fix.</summary>
    Task<ClaimedFinding?> ClaimNextForFixAsync(
        Guid planId,
        Guid actorId,
        TimeSpan ttl,
        int retryCap,
        CancellationToken ct = default);

    Task<Finding?> UpdateStatusWithClaimAsync(
        Guid claimToken, Guid actorId, string newStatus, bool clearClaim,
        string eventType, string? eventPayloadJson,
        CancellationToken ct = default);

    /// <summary>
    /// Extend an active claim's TTL on a finding. Returns the updated row with the new
    /// claimed_until, or null if the claim token is unknown or already expired (caller
    /// surfaces <c>stale_claim</c>). Writes a <c>claim_refreshed</c> event.
    /// </summary>
    Task<Finding?> RefreshClaimAsync(
        Guid claimToken,
        Guid actorId,
        TimeSpan extendBy,
        CancellationToken ct = default);

    /// <summary>
    /// Override the finding status without holding a claim. Caller must have already
    /// verified <c>can_override_verdict</c>. Writes an <c>override</c> event and clears
    /// any held claim. The new status must be a valid finding state for the plan type;
    /// status validity is enforced at the application layer (this method only writes
    /// what it is told).
    /// </summary>
    Task<Finding?> OverrideStatusAsync(
        Guid findingId,
        Guid actorId,
        string newStatus,
        string reason,
        CancellationToken ct = default);

    Task<int> SweepExpiredClaimsAsync(CancellationToken ct = default);

    Task<bool> AllInTerminalResolvedAsync(Guid taskId, CancellationToken ct = default);
}

public sealed record ClaimedFinding(Finding Finding, Guid ClaimToken);
