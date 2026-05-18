using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;

namespace Workstream.Data.Repositories;

public interface IPlanTypeRepository
{
    Task<PlanType?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PlanType>> ListAsync(CancellationToken ct = default);
}
