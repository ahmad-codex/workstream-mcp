using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;

namespace Workstream.Mcp.Tools.Setup;

// ============================================================================
// request_audit — the single entry point for "run full audit on project X, Y…"
// ============================================================================

public sealed record RequestAuditInput(string[] Projects);

public sealed record RequestAuditResult(string Project, string Status, long? DispatchId, Guid? ProjectId);
public sealed record RequestAuditOutput(IReadOnlyList<RequestAuditResult> Results);

/// <summary>
/// Queues a full-audit run for one or more projects. Each project name is resolved (by
/// slug or id) and a row is written to the <c>audit_dispatch_log</c> outbox; the
/// AuditDispatchWorker drains it and launches that project's audit orchestrator. This is
/// the central control point — one call fans out audits across many projects without the
/// caller checking out any repo.
/// </summary>
public sealed class RequestAuditTool : McpTool<RequestAuditInput, RequestAuditOutput>
{
    private readonly IProjectRepository _projects;
    private readonly IOutboxRepository _outbox;

    public RequestAuditTool(IProjectRepository projects, IOutboxRepository outbox)
    {
        _projects = projects; _outbox = outbox;
    }

    public override string Name => "request_audit";
    public override string Description =>
        "Admin: request a full audit run for one or more projects. Pass `projects` as a list " +
        "of project slugs or ids. Each is resolved and queued on the audit-dispatch outbox; a " +
        "background worker then launches that project's audit orchestrator (no repo checkout by " +
        "the caller). The result lists each project with status 'queued' (a dispatch id is " +
        "returned) or 'unknown_project' (the name did not resolve). Use this as the single entry " +
        "point for \"run full audit on X\" across the fleet.";

    protected override async Task<RequestAuditOutput> RunAsync(RequestAuditInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);

        var results = new List<RequestAuditResult>();
        foreach (var name in input.Projects ?? Array.Empty<string>())
        {
            var key = (name ?? "").Trim();
            if (key.Length == 0) continue;

            var project = await ResolveAsync(key, ct).ConfigureAwait(false);
            if (project is null)
            {
                results.Add(new RequestAuditResult(key, "unknown_project", null, null));
                continue;
            }

            var dispatchId = await _outbox.EnqueueAuditDispatchAsync(project.Id, ctx.ActorId, ct).ConfigureAwait(false);
            results.Add(new RequestAuditResult(project.Slug, "queued", dispatchId, project.Id));
        }
        return new RequestAuditOutput(results);
    }

    /// <summary>Resolve a project by id (when the key parses as a Guid) or by slug.</summary>
    private async Task<Project?> ResolveAsync(string key, CancellationToken ct)
        => Guid.TryParse(key, out var id)
            ? await _projects.GetAsync(id, ct).ConfigureAwait(false)
            : await _projects.GetBySlugAsync(key, ct).ConfigureAwait(false);
}
