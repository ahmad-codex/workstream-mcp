using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;
using TaskStatus = Workstream.Core.Domain.TaskStatus;

namespace Workstream.GitHub;

/// <summary>
/// Background hosted service that drains the GitHub outboxes — <c>board_sync_log</c> (per
/// task) and <c>milestone_sync_log</c> (per plan). One instance per API replica.
///
/// Tasks on a repo-backed project are created as real GitHub <b>issues</b> assigned to the
/// plan's audit milestone, then added to the Projects V2 board; their status flips the
/// board's Status field and closes/reopens the issue. A project with no registered repo
/// falls back to a Projects V2 draft item (the legacy path).
///
/// The milestone is created when the plan is created and closed (with a Completed/Canceled
/// title suffix + reason) when the plan is archived.
/// </summary>
public sealed class BoardSyncWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 16;
    private const int MaxAttempts = 5;
    private const int MilestoneWaitAttempts = 4;   // task syncs defer this many times for the milestone

    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;
    private readonly IPlanRepository _plans;
    private readonly ITaskRepository _tasks;
    private readonly IFindingRepository _findings;
    private readonly IAttemptRepository _attempts;
    private readonly IEventRepository _events;
    private readonly ProjectsV2Client _gh;
    private readonly ILogger<BoardSyncWorker> _log;

    private readonly ConcurrentDictionary<Guid, BoardFields> _fieldCache = new();

    public BoardSyncWorker(IOutboxRepository outbox, IProjectRepository projects, IPlanRepository plans,
        ITaskRepository tasks, IFindingRepository findings, IAttemptRepository attempts,
        IEventRepository events, ProjectsV2Client gh, ILogger<BoardSyncWorker> log)
    {
        _outbox = outbox; _projects = projects; _plans = plans; _tasks = tasks;
        _findings = findings; _attempts = attempts; _events = events;
        _gh = gh; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var milestones = await _outbox.ClaimMilestoneSyncBatchAsync(BatchSize, stoppingToken).ConfigureAwait(false);
                foreach (var m in milestones)
                    await ProcessMilestoneAsync(m, stoppingToken).ConfigureAwait(false);

                var batch = await _outbox.ClaimBoardSyncBatchAsync(BatchSize, stoppingToken).ConfigureAwait(false);
                foreach (var row in batch)
                    await ProcessAsync(row, stoppingToken).ConfigureAwait(false);

                if (milestones.Count == 0 && batch.Count == 0)
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "board-sync worker loop failed; retrying after delay");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    // ===== Milestone outbox =====

    private async Task ProcessMilestoneAsync(MilestoneSyncRow row, CancellationToken ct)
    {
        try
        {
            var plan = await _plans.GetAsync(row.PlanId, ct).ConfigureAwait(false);
            if (plan is null)
            {
                await _outbox.MarkMilestoneSyncResultAsync(row.Id, "failed", "plan not found", null, ct).ConfigureAwait(false);
                return;
            }
            var repo = await ResolvePrimaryRepoAsync(plan.ProjectId, ct).ConfigureAwait(false);
            if (repo is null)
            {
                // No repo => no milestone is possible; nothing to do, not an error.
                await _outbox.MarkMilestoneSyncResultAsync(row.Id, "success", null, null, ct).ConfigureAwait(false);
                return;
            }

            if (row.Action == "create")
            {
                if (plan.GithubMilestoneNumber is null)
                {
                    var title = $"{plan.Name} ({plan.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC)";
                    var desc  = $"Audit run for plan **{plan.Name}**.\nStarted: {plan.CreatedAt.UtcDateTime:u}";
                    var number = await _gh.CreateMilestoneAsync(repo.Value.Owner, repo.Value.Repo, title, desc, ct)
                        .ConfigureAwait(false);
                    await _plans.SetMilestoneNumberAsync(plan.Id, number, ct).ConfigureAwait(false);
                    _log.LogInformation("created milestone #{Number} for plan {Plan}", number, plan.Id);
                }
            }
            else if (row.Action == "close")
            {
                if (plan.GithubMilestoneNumber is { } num)
                {
                    var outcome = string.IsNullOrWhiteSpace(row.Outcome) ? "Completed" : row.Outcome!;
                    var current = await _gh.GetMilestoneTitleAsync(repo.Value.Owner, repo.Value.Repo, num, ct)
                        .ConfigureAwait(false);
                    var title = current is null || current.Contains(" — ", StringComparison.Ordinal)
                        ? current
                        : $"{current} — {outcome}";
                    var desc = $"Outcome: {outcome}\nReason: {(string.IsNullOrWhiteSpace(row.Reason) ? "(none)" : row.Reason)}\n"
                             + $"Finished: {DateTimeOffset.UtcNow:u}";
                    await _gh.UpdateMilestoneAsync(repo.Value.Owner, repo.Value.Repo, num,
                        title: title, description: desc, state: "closed", ct: ct).ConfigureAwait(false);
                    _log.LogInformation("closed milestone #{Number} ({Outcome}) for plan {Plan}", num, outcome, plan.Id);
                }
            }
            await _outbox.MarkMilestoneSyncResultAsync(row.Id, "success", null, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (row.Attempts >= MaxAttempts)
            {
                _log.LogWarning(ex, "milestone sync row {Id} failed at max attempts", row.Id);
                await _outbox.MarkMilestoneSyncResultAsync(row.Id, "failed", ex.Message, null, ct).ConfigureAwait(false);
            }
            else
            {
                await _outbox.MarkMilestoneSyncResultAsync(row.Id, "retry", ex.Message,
                    TimeSpan.FromSeconds(Math.Pow(2, row.Attempts)), ct).ConfigureAwait(false);
            }
        }
    }

    // ===== Board-sync outbox (per task) =====

    private async Task ProcessAsync(BoardSyncRow row, CancellationToken ct)
    {
        if (row.BoardId is null || row.TaskId is null)
        {
            await _outbox.MarkBoardSyncResultAsync(row.Id, "failed", "missing board/task id", null, null, ct).ConfigureAwait(false);
            return;
        }
        try
        {
            var board = await _projects.GetBoardAsync(row.BoardId.Value, ct).ConfigureAwait(false);
            if (board is null)
            {
                await _outbox.MarkBoardSyncResultAsync(row.Id, "failed", "board not found", null, null, ct).ConfigureAwait(false);
                return;
            }
            var task = await _tasks.GetAsync(row.TaskId.Value, ct).ConfigureAwait(false);
            if (task is null)
            {
                await _outbox.MarkBoardSyncResultAsync(row.Id, "failed", "task not found", null, null, ct).ConfigureAwait(false);
                return;
            }
            var plan = await _plans.GetAsync(task.PlanId, ct).ConfigureAwait(false);
            var repo = plan is null ? null : await ResolvePrimaryRepoAsync(plan.ProjectId, ct).ConfigureAwait(false);

            var fields = await ResolveBoardFieldsAsync(board, ct).ConfigureAwait(false);
            var optionId = row.TargetColumn switch
            {
                "backlog"     => board.StatusOptionBacklog,
                "in_progress" => board.StatusOptionInProgress,
                "review"      => board.StatusOptionReview,
                "done"        => board.StatusOptionDone,
                "blocked"     => fields.BlockedOptionId ?? board.StatusOptionBacklog,
                _             => board.StatusOptionBacklog,
            };

            var issueNumber = task.GithubIssueNumber;
            var itemId = task.GithubBoardItemId;

            // Lazy create. A repo-backed project gets a real issue (assigned to the plan's
            // milestone) added to the board; a repo-less project keeps the draft-item path.
            if (string.IsNullOrEmpty(itemId))
            {
                if (repo is { } r)
                {
                    // Wait a few attempts for the plan's milestone to be created so the
                    // issue can be assigned to it; then proceed without it rather than block.
                    if (plan!.GithubMilestoneNumber is null && row.Attempts < MilestoneWaitAttempts)
                    {
                        await _outbox.MarkBoardSyncResultAsync(row.Id, "retry", null, null, TimeSpan.FromSeconds(5), ct)
                            .ConfigureAwait(false);
                        return;
                    }
                    var issue = await _gh.CreateIssueAsync(r.Owner, r.Repo, task.Title, task.Description ?? "",
                        plan.GithubMilestoneNumber, ct).ConfigureAwait(false);
                    issueNumber = issue.Number;
                    await _tasks.SetGithubIssueAsync(task.Id, issue.Number, issue.NodeId, ct).ConfigureAwait(false);

                    var item = await _gh.AddIssueToProjectAsync(board.GithubProjectV2NodeId, issue.NodeId, ct)
                        .ConfigureAwait(false);
                    itemId = item.NodeId;
                    await _tasks.SetGithubBoardItemIdAsync(task.Id, itemId, item.DatabaseId, ct).ConfigureAwait(false);
                    _log.LogInformation("created issue #{Issue} + board item {ItemId} for task {TaskId}",
                        issue.Number, itemId, task.Id);
                }
                else
                {
                    var created = await _gh.CreateDraftItemAsync(
                        board.GithubProjectV2NodeId, task.Title, task.Description ?? "", ct).ConfigureAwait(false);
                    itemId = created.NodeId;
                    await _tasks.SetGithubBoardItemIdAsync(task.Id, itemId, created.DatabaseId, ct).ConfigureAwait(false);
                    _log.LogInformation("created draft item {ItemId} for task {TaskId}", itemId, task.Id);
                }
            }
            else if (await _tasks.GetGithubBoardItemNumberAsync(task.Id, ct).ConfigureAwait(false) is null)
            {
                var num = await _gh.LookupItemDatabaseIdAsync(itemId, ct).ConfigureAwait(false);
                if (num is not null)
                    await _tasks.SetGithubBoardItemIdAsync(task.Id, itemId, num, ct).ConfigureAwait(false);
            }

            await _gh.UpdateItemStatusFieldAsync(
                board.GithubProjectV2NodeId, itemId, board.StatusFieldNodeId, optionId, ct).ConfigureAwait(false);

            // Status changes propagate to the issue too: closed when the task is done.
            if (issueNumber is { } inum && repo is { } ir)
            {
                try
                {
                    await _gh.UpdateIssueAsync(ir.Owner, ir.Repo, inum,
                        state: row.TargetColumn == "done" ? "closed" : "open", ct: ct).ConfigureAwait(false);
                }
                catch (Exception issueEx)
                {
                    _log.LogWarning(issueEx, "issue state update failed for task {TaskId}", task.Id);
                }
            }

            await ApplyAssigneesAsync(row, task, issueNumber, repo, itemId, ct).ConfigureAwait(false);

            try
            {
                await RefreshCardAsync(board, task, itemId, issueNumber, repo, fields, ct).ConfigureAwait(false);
            }
            catch (Exception cardEx)
            {
                _log.LogWarning(cardEx, "audit-ledger refresh failed for task {TaskId}; column flip already succeeded", row.TaskId);
            }

            await _outbox.MarkBoardSyncResultAsync(row.Id, "success", null, itemId, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var nextAttempt = row.Attempts + 1;
            if (nextAttempt >= MaxAttempts)
            {
                _log.LogWarning(ex, "board sync row {Id} failed at max attempts", row.Id);
                await _outbox.MarkBoardSyncResultAsync(row.Id, "failed", ex.Message, null, null, ct).ConfigureAwait(false);
            }
            else
            {
                await _outbox.MarkBoardSyncResultAsync(row.Id, "retry", ex.Message, null,
                    TimeSpan.FromSeconds(Math.Pow(2, nextAttempt)), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Apply the row's assignee directive. For an issue-backed task this is a REST issue
    /// assignee patch (by login); for a draft item it uses the Projects V2 draft mutation.
    /// Best-effort — a missing GitHub user must not fail the column flip.
    /// </summary>
    private async Task ApplyAssigneesAsync(BoardSyncRow row, WorkTask task, int? issueNumber,
        (string Owner, string Repo)? repo, string itemId, CancellationToken ct)
    {
        if (row.AssigneeGithubUsername is null) return;   // null => don't touch assignees
        try
        {
            if (issueNumber is { } inum && repo is { } r)
            {
                var assignees = row.AssigneeGithubUsername.Length == 0
                    ? Array.Empty<string>()
                    : new[] { row.AssigneeGithubUsername };
                await _gh.UpdateIssueAsync(r.Owner, r.Repo, inum, assignees: assignees, ct: ct).ConfigureAwait(false);
            }
            else if (row.AssigneeGithubUsername.Length == 0)
            {
                var draftId = await _gh.LookupDraftIssueIdAsync(itemId, ct).ConfigureAwait(false);
                if (draftId is not null)
                    await _gh.UpdateDraftIssueAssigneesAsync(draftId, Array.Empty<string>(), ct).ConfigureAwait(false);
            }
            else
            {
                var (draftId, userId) = await _gh.LookupDraftAndUserAsync(itemId, row.AssigneeGithubUsername, ct).ConfigureAwait(false);
                if (draftId is not null && userId is not null)
                    await _gh.UpdateDraftIssueAssigneesAsync(draftId, new[] { userId }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "assignee update failed for task {TaskId}; column flip already succeeded", task.Id);
        }
    }

    /// <summary>
    /// Re-render the task's card/issue body as an audit ledger and set the optional
    /// "Audit Stage" field. Only audit tasks (with findings) or blocked tasks get a body
    /// rewrite. For an issue-backed task the body is the issue body; otherwise the draft.
    /// </summary>
    private async Task RefreshCardAsync(ProjectBoard board, WorkTask task, string itemId, int? issueNumber,
        (string Owner, string Repo)? repo, BoardFields fields, CancellationToken ct)
    {
        var findings = await _findings.ListByTaskAsync(task.Id, ct).ConfigureAwait(false);
        var isBlocked = task.Status is TaskStatus.Blocked or TaskStatus.NeedsHumanReview;
        if (findings.Count == 0 && !isBlocked)
            return;

        var commitByFinding = new Dictionary<Guid, string>();
        foreach (var f in findings)
        {
            if (f.Status != FindingStatus.Fixed) continue;
            var attempts = await _attempts.ListForFindingAsync(f.Id, ct).ConfigureAwait(false);
            for (var i = attempts.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(attempts[i].CommitHash))
                {
                    commitByFinding[f.Id] = ShortHash(attempts[i].CommitHash!);
                    break;
                }
            }
        }

        var blockReason = isBlocked ? await ResolveBlockReasonAsync(task.Id, ct).ConfigureAwait(false) : null;
        var body = AuditLedger.Render(task, findings, commitByFinding, blockReason, DateTimeOffset.UtcNow);

        if (issueNumber is { } inum && repo is { } r)
        {
            await _gh.UpdateIssueAsync(r.Owner, r.Repo, inum, body: body, ct: ct).ConfigureAwait(false);
        }
        else
        {
            var draftId = await _gh.LookupDraftIssueIdAsync(itemId, ct).ConfigureAwait(false);
            if (draftId is not null)
                await _gh.UpdateDraftIssueBodyAsync(draftId, body, ct).ConfigureAwait(false);
        }

        if (findings.Count > 0 && fields.AuditStageFieldId is { } stageField)
        {
            var stage = AuditLedger.ComputeStage(task.Status, findings);
            if (fields.AuditStageOptions.TryGetValue(stage, out var optId))
                await _gh.UpdateItemStatusFieldAsync(board.GithubProjectV2NodeId, itemId, stageField, optId, ct)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>The project's primary (non reference-only) repo, or null when none is registered.</summary>
    private async Task<(string Owner, string Repo)?> ResolvePrimaryRepoAsync(Guid projectId, CancellationToken ct)
    {
        var repos = await _projects.ListReposAsync(projectId, ct).ConfigureAwait(false);
        var primary = repos.FirstOrDefault(x => !x.IsReferenceOnly) ?? repos.FirstOrDefault();
        return primary is null ? null : (primary.GithubOwner, primary.GithubRepo);
    }

    private async Task<string?> ResolveBlockReasonAsync(Guid taskId, CancellationToken ct)
    {
        try
        {
            var events = await _events.ListForEntityAsync(EntityType.Task, taskId, since: null, limit: 30, ct)
                .ConfigureAwait(false);
            foreach (var e in events.OrderByDescending(e => e.At))
            {
                if (e.ToState is not (TaskStatus.Blocked or TaskStatus.NeedsHumanReview)) continue;
                if (string.IsNullOrWhiteSpace(e.Payload)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(e.Payload);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("reason", out var r)
                        && r.ValueKind == JsonValueKind.String
                        && r.GetString() is { Length: > 0 } reason)
                    {
                        return reason;
                    }
                }
                catch (JsonException) { /* try the next event */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "block-reason lookup failed for task {TaskId}", taskId);
        }
        return null;
    }

    private async Task<BoardFields> ResolveBoardFieldsAsync(ProjectBoard board, CancellationToken ct)
    {
        if (_fieldCache.TryGetValue(board.Id, out var cached)) return cached;

        string? blockedOpt = board.StatusOptionBlocked;
        if (string.IsNullOrEmpty(blockedOpt))
        {
            try
            {
                var statusOpts = await _gh.GetStatusOptionsAsync(board.GithubProjectV2NodeId, ct: ct).ConfigureAwait(false);
                foreach (var kv in statusOpts)
                    if (kv.Key.Equals("Blocked", StringComparison.OrdinalIgnoreCase)) { blockedOpt = kv.Value; break; }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Blocked status-option discovery failed for board {Board}", board.Id);
            }
        }

        string? stageFieldId = null;
        IReadOnlyDictionary<string, string> stageOpts = new Dictionary<string, string>();
        try
        {
            var field = await _gh.GetSingleSelectFieldAsync(board.GithubProjectV2NodeId, "Audit Stage", ct)
                .ConfigureAwait(false);
            if (field is not null)
            {
                stageFieldId = field.FieldId;
                stageOpts = field.Options;
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Audit Stage field discovery failed for board {Board}", board.Id);
        }

        var fields = new BoardFields(blockedOpt, stageFieldId, stageOpts);
        _fieldCache[board.Id] = fields;
        return fields;
    }

    private static string ShortHash(string hash) => hash.Length <= 10 ? hash : hash[..10];

    private sealed record BoardFields(
        string? BlockedOptionId,
        string? AuditStageFieldId,
        IReadOnlyDictionary<string, string> AuditStageOptions);
}
