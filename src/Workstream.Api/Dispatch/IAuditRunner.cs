using System;
using System.Threading;
using System.Threading.Tasks;

namespace Workstream.Api.Dispatch;

/// <summary>The project a queued audit dispatch should run against.</summary>
public sealed record AuditRunContext(Guid ProjectId, string ProjectSlug, string? RepoOwner, string? RepoName);

/// <summary>Outcome of one dispatch run. <see cref="Summary"/> / <see cref="Error"/> are persisted on the outbox row.</summary>
public sealed record AuditRunResult(bool Ok, string? Summary, string? Error);

/// <summary>
/// Launches a project's audit orchestrator. Abstracted so the <see cref="AuditDispatchWorker"/>
/// (drain / retry / mark logic) is unit-testable without shelling out, and so the actual
/// launch mechanism can change without touching the worker.
/// </summary>
public interface IAuditRunner
{
    Task<AuditRunResult> RunAsync(AuditRunContext ctx, CancellationToken ct);
}
