using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Workstream.Api.Dispatch;

/// <summary>A GitHub repo reference (owner + name).</summary>
public sealed record RepoRef(string Owner, string Name);

/// <summary>
/// The project a queued audit dispatch should run against.
/// <see cref="WorkstreamMcpToken"/> is the triggering user's MCP token — the audit runs
/// on their behalf — and is a credential (never log it). <see cref="ReferenceRepos"/>
/// are read-only repos the orchestrator wants checked out alongside the primary one.
/// </summary>
public sealed record AuditRunContext(
    Guid ProjectId,
    string ProjectSlug,
    string? RepoOwner,
    string? RepoName,
    string? WorkstreamMcpToken,
    IReadOnlyList<RepoRef> ReferenceRepos);

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
