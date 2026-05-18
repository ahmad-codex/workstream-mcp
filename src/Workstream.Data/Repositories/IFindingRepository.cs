using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IFindingRepository
{
    Task<Finding?> GetAsync(Guid id, CancellationToken ct = default);
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

    Task<int> SweepExpiredClaimsAsync(CancellationToken ct = default);

    Task<bool> AllInTerminalResolvedAsync(Guid taskId, CancellationToken ct = default);
}

public sealed record ClaimedFinding(Finding Finding, Guid ClaimToken);
