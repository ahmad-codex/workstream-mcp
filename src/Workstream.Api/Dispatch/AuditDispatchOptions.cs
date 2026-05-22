namespace Workstream.Api.Dispatch;

/// <summary>
/// Configuration for the audit-dispatch worker. The worker stays idle unless
/// <see cref="Enabled"/> is set, so the feature can ship dark and be turned on per
/// deployment once the host has the dispatch script, Claude Code, and the repos in place.
/// </summary>
public sealed class AuditDispatchOptions
{
    /// <summary>When false the worker does not poll the outbox at all. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute path to the dispatch script the worker runs per queued audit. The script
    /// receives the project as environment variables (<c>WS_PROJECT_ID</c>,
    /// <c>WS_PROJECT_SLUG</c>, <c>WS_REPO_OWNER</c>, <c>WS_REPO_NAME</c>) and is expected
    /// to check out the repo and launch its audit orchestrator (e.g. <c>claude -p
    /// /audit-run</c>). See <c>deploy/audit-dispatch.sh.example</c>.
    /// </summary>
    public string ScriptPath { get; set; } = "";

    /// <summary>Hard timeout for one dispatch run before the worker kills the process.</summary>
    public int TimeoutSeconds { get; set; } = 1800;
}
