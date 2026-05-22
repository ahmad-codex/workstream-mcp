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

public sealed record RequestAuditResult(
    string Project, string Status, long? DispatchId, Guid? ProjectId, string? AttachCommand = null);
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
        "the caller). The result lists each project with status 'queued' (with a dispatch id and " +
        "an `attach_command` — the command to watch the live audit session once SSH'd into the " +
        "server) or 'unknown_project' (the name did not resolve).";

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

            var dispatchId = await _outbox.EnqueueAuditDispatchAsync(project.Id, ctx.ActorId, "run", ct).ConfigureAwait(false);
            var attach = $"docker exec -it deploy-dispatcher-1 tmux attach -t audit-{project.Slug}";
            results.Add(new RequestAuditResult(project.Slug, "queued", dispatchId, project.Id, attach));
        }
        return new RequestAuditOutput(results);
    }

    /// <summary>Resolve a project by id (when the key parses as a Guid) or by slug.</summary>
    private async Task<Project?> ResolveAsync(string key, CancellationToken ct)
        => Guid.TryParse(key, out var id)
            ? await _projects.GetAsync(id, ct).ConfigureAwait(false)
            : await _projects.GetBySlugAsync(key, ct).ConfigureAwait(false);
}

// ============================================================================
// cancel_audit — stop a running audit; the orchestrator does its own cleanup
// ============================================================================

public sealed record CancelAuditInput(string[] Projects);
public sealed record CancelAuditOutput(IReadOnlyList<RequestAuditResult> Results);

/// <summary>
/// Requests cancellation of a running audit for one or more projects. Each project name
/// is resolved and a 'cancel' row is queued on the audit-dispatch outbox; the background
/// worker then signals that project's running audit session to stop — the orchestrator
/// runs its own <c>/audit-cancel</c> cleanup (releasing claims, discarding worktrees)
/// rather than being hard-killed. The result lists each project with status 'queued' or
/// 'unknown_project'. Symmetric to request_audit.
/// </summary>
public sealed class CancelAuditTool : McpTool<CancelAuditInput, CancelAuditOutput>
{
    private readonly IProjectRepository _projects;
    private readonly IOutboxRepository _outbox;

    public CancelAuditTool(IProjectRepository projects, IOutboxRepository outbox)
    {
        _projects = projects; _outbox = outbox;
    }

    public override string Name => "cancel_audit";
    public override string Description =>
        "Admin: cancel a running audit for one or more projects. Pass `projects` as a list " +
        "of project slugs or ids. Each is resolved and a cancel request is queued on the " +
        "audit-dispatch outbox; a background worker signals that project's audit session, " +
        "which triggers the orchestrator's own /audit-cancel cleanup (no hard kill). The " +
        "result lists each project with status 'queued' or 'unknown_project'.";

    protected override async Task<CancelAuditOutput> RunAsync(CancelAuditInput input, RequestContext ctx, CancellationToken ct)
    {
        AdminGate.Require(ctx);

        var results = new List<RequestAuditResult>();
        foreach (var name in input.Projects ?? Array.Empty<string>())
        {
            var key = (name ?? "").Trim();
            if (key.Length == 0) continue;

            var project = Guid.TryParse(key, out var id)
                ? await _projects.GetAsync(id, ct).ConfigureAwait(false)
                : await _projects.GetBySlugAsync(key, ct).ConfigureAwait(false);
            if (project is null)
            {
                results.Add(new RequestAuditResult(key, "unknown_project", null, null));
                continue;
            }

            var dispatchId = await _outbox.EnqueueAuditDispatchAsync(project.Id, ctx.ActorId, "cancel", ct)
                .ConfigureAwait(false);
            results.Add(new RequestAuditResult(project.Slug, "queued", dispatchId, project.Id));
        }
        return new CancelAuditOutput(results);
    }
}
