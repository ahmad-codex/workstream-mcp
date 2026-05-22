using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Workstream.Api.Dispatch;

/// <summary>
/// Default <see cref="IAuditRunner"/>: runs the configured dispatch script via
/// <c>/bin/bash</c>, passing the project as environment variables. The script owns the
/// messy part — checking out the repo and launching the audit orchestrator — so the C#
/// side stays a thin, packaging-agnostic launcher.
/// </summary>
public sealed class ProcessAuditRunner : IAuditRunner
{
    private readonly AuditDispatchOptions _opts;
    private readonly ILogger<ProcessAuditRunner> _log;

    public ProcessAuditRunner(IOptions<AuditDispatchOptions> opts, ILogger<ProcessAuditRunner> log)
    {
        _opts = opts.Value; _log = log;
    }

    public async Task<AuditRunResult> RunAsync(AuditRunContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.ScriptPath) || !File.Exists(_opts.ScriptPath))
            return new AuditRunResult(false, null, $"dispatch script not found: '{_opts.ScriptPath}'");

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(_opts.ScriptPath);
        psi.Environment["WS_PROJECT_ID"]   = ctx.ProjectId.ToString();
        psi.Environment["WS_PROJECT_SLUG"] = ctx.ProjectSlug;
        psi.Environment["WS_REPO_OWNER"]   = ctx.RepoOwner ?? "";
        psi.Environment["WS_REPO_NAME"]    = ctx.RepoName ?? "";
        // The triggering user's MCP token — a credential; passed to the script only as an
        // environment variable, never logged.
        psi.Environment["WS_MCP_TOKEN"]    = ctx.WorkstreamMcpToken ?? "";
        psi.Environment["WS_REFERENCE_REPOS"] =
            string.Join(' ', ctx.ReferenceRepos.Select(r => $"{r.Owner}/{r.Name}"));

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            return new AuditRunResult(false, null, $"failed to start dispatch script: {ex.Message}");
        }

        // Bound the run by the configured timeout, and by the host's shutdown token.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _opts.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            var reason = timeout.IsCancellationRequested ? "timed out" : "cancelled";
            return new AuditRunResult(false, null, $"dispatch {reason} for {ctx.ProjectSlug}");
        }

        var tail = Tail(stderr.Length > 0 ? stderr.ToString() : stdout.ToString(), 500);
        return proc.ExitCode == 0
            ? new AuditRunResult(true, $"audit session launched for {ctx.ProjectSlug}", null)
            : new AuditRunResult(false, null, $"dispatch script exited {proc.ExitCode}: {tail}");
    }

    private void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception ex) { _log.LogWarning(ex, "failed to kill timed-out dispatch process"); }
    }

    private static string Tail(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }
}
