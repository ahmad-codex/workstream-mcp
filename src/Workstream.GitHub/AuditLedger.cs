using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Workstream.Core.Domain;
using TaskStatus = Workstream.Core.Domain.TaskStatus;

namespace Workstream.GitHub;

/// <summary>
/// Renders the live "audit ledger" written into a task's Project V2 card body.
///
/// An audit task's card moves between four board columns (Backlog → In Progress →
/// Review → Done / Blocked), but the real work — the per-finding verify → fix →
/// fix-verify lifecycle — happens entirely inside the Review column and would
/// otherwise be invisible on the board. The ledger surfaces it: the task description,
/// every finding with its current state and (for fixed findings) the closing commit,
/// and a banner when the task is blocked or escalated.
///
/// <see cref="ComputeStage"/> additionally collapses the finding mix to a single
/// coarse stage that drives the optional "Audit Stage" board field.
/// </summary>
public static class AuditLedger
{
    /// <summary>The coarse stages <see cref="ComputeStage"/> can return. An operator who
    /// wants the at-a-glance board field creates a single-select "Audit Stage" field
    /// with (a subset of) these option names.</summary>
    public static readonly IReadOnlyList<string> Stages = new[]
    {
        "Scanning", "Verifying", "Fixing", "Re-Verifying", "Escalated", "Blocked", "Clean", "Done",
    };

    public static string Render(
        WorkTask task,
        IReadOnlyList<Finding> findings,
        IReadOnlyDictionary<Guid, string> commitByFinding,
        string? blockReason,
        DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("### ").Append(task.ExternalKey).Append(" — ").Append(task.Title).Append('\n');

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.Append('\n').Append(task.Description!.Trim()).Append('\n');
        }

        if (findings.Count > 0)
        {
            sb.Append("\n---\n\n");
            sb.Append("**Audit ledger** — stage: ").Append(ComputeStage(task.Status, findings))
              .Append(" · ").Append(findings.Count).Append(findings.Count == 1 ? " finding" : " findings");
            var breakdown = Breakdown(findings);
            if (breakdown.Length > 0) sb.Append(" (").Append(breakdown).Append(')');
            sb.Append("\n\n");

            foreach (var f in findings.OrderBy(x => x.ExternalKey, StringComparer.Ordinal))
            {
                sb.Append("- ").Append(Marker(f.Status)).Append(" `").Append(f.ExternalKey).Append("` ")
                  .Append('*').Append(Humanize(f.Status)).Append('*');
                if (!string.IsNullOrWhiteSpace(f.Severity))
                    sb.Append(" _(").Append(f.Severity).Append(")_");
                var summary = ShortSummary(f.Symptom);
                if (summary.Length > 0) sb.Append(" — ").Append(summary);
                if (f.Status == FindingStatus.Fixed
                    && commitByFinding.TryGetValue(f.Id, out var commit) && commit.Length > 0)
                    sb.Append(" — commit `").Append(commit).Append('`');
                sb.Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            var banner = task.Status == TaskStatus.NeedsHumanReview
                ? "🚨 **Needs human review:** "
                : "⛔ **Blocked:** ";
            sb.Append("\n> ").Append(banner).Append(blockReason!.Trim()).Append('\n');
        }

        sb.Append("\n_Synced from workstream-mcp · ")
          .Append(now.ToUniversalTime().ToString("yyyy-MM-dd HH:mm"))
          .Append(" UTC_");
        return sb.ToString();
    }

    /// <summary>
    /// Collapse the task status + finding mix to one coarse stage. Precedence runs from
    /// the most urgent (blocked / escalated) to the most settled (done / clean).
    /// </summary>
    public static string ComputeStage(string taskStatus, IReadOnlyList<Finding> findings)
    {
        if (taskStatus == TaskStatus.Blocked) return "Blocked";
        if (taskStatus == TaskStatus.NeedsHumanReview) return "Escalated";
        if (findings.Any(f => f.Status == FindingStatus.NeedsHumanReview)) return "Escalated";

        if (findings.Count == 0)
            return taskStatus == TaskStatus.Done ? "Clean" : "Scanning";
        if (taskStatus == TaskStatus.Done) return "Done";

        if (findings.Any(f => f.Status is FindingStatus.Confirmed or FindingStatus.InFix
                              or FindingStatus.FixFailed or FindingStatus.Partial))
            return "Fixing";
        if (findings.Any(f => f.Status is FindingStatus.PendingVerification or FindingStatus.Ambiguous))
            return "Verifying";
        // Every finding is in a terminal state but the task has not rolled up yet.
        return "Re-Verifying";
    }

    private static string Marker(string status) => status switch
    {
        FindingStatus.Fixed or FindingStatus.Rejected or FindingStatus.Deferred => "[x]",
        FindingStatus.NeedsHumanReview => "⚠️",
        _ => "[ ]",
    };

    private static string Breakdown(IReadOnlyList<Finding> findings)
    {
        // Display order: settled-good, in-flight, settled-other.
        var order = new[]
        {
            FindingStatus.Fixed, FindingStatus.Confirmed, FindingStatus.InFix,
            FindingStatus.FixFailed, FindingStatus.Partial, FindingStatus.PendingVerification,
            FindingStatus.Ambiguous, FindingStatus.Rejected, FindingStatus.NeedsHumanReview,
            FindingStatus.Deferred,
        };
        var counts = findings.GroupBy(f => f.Status).ToDictionary(g => g.Key, g => g.Count());
        var parts = new List<string>();
        foreach (var st in order)
            if (counts.TryGetValue(st, out var n) && n > 0)
                parts.Add($"{n} {Humanize(st)}");
        return string.Join(" · ", parts);
    }

    private static string Humanize(string status) => status.Replace('_', ' ');

    private static string ShortSummary(string? text, int max = 100)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Regex.Replace(text.Trim(), @"\s+", " ");
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }
}
