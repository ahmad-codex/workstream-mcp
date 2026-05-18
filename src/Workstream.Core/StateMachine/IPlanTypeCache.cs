using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Core.StateMachine;

/// <summary>
/// Loads and caches plan-type rows + their parsed <see cref="StateGraph"/>.
/// The concrete implementation in <c>Workstream.Data</c> backs this with Postgres and listens
/// to <c>workstream_plan_types_changed</c> NOTIFY for invalidation. Tests use the in-memory
/// implementation below.
/// </summary>
public interface IPlanTypeCache
{
    Task<(PlanType Row, StateGraph Graph)?> GetAsync(string planTypeId, CancellationToken ct = default);

    /// <summary>Invalidate a single plan type. Called by the LISTEN/NOTIFY worker.</summary>
    void Invalidate(string planTypeId);
}
