using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Background hosted service that drains <c>board_sync_log</c> (§7.5). One instance per API
/// replica. Exponential backoff on failure, capped at 5 attempts before the row is marked
/// <c>failed</c> and a <c>board_sync_failed</c> event is emitted.
///
/// Lazy item creation: the worker treats <c>tasks.github_board_item_id</c> as the source of
/// truth. If a task has never been synced, the worker calls <c>addProjectV2DraftIssue</c>
/// first, persists the returned <c>PVTI_…</c> back onto the task row, then updates the
/// Status field.
///
/// Audit visibility: after the column flip the worker also re-renders the card's body as a
/// live audit ledger (every finding + state + closing commit + block reason) and, when the
/// board has an "Audit Stage" single-select field, sets it to the computed stage. This is
/// how the per-finding audit lifecycle — which never moves the card between columns — stays
/// visible on the board.
/// </summary>
public sealed class BoardSyncWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 16;
    private const int MaxAttempts = 5;

    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly IFindingRepository _findings;
    private readonly IAttemptRepository _attempts;
    private readonly IEventRepository _events;
    private readonly ProjectsV2Client _gh;
    private readonly ILogger<BoardSyncWorker> _log;

    // Per-board field metadata (Blocked status option, optional "Audit Stage" field),
    // resolved from GitHub once per process. A deploy restart re-discovers — so adding
    // the Blocked column or the Audit Stage field on GitHub takes effect after a restart.
    private readonly ConcurrentDictionary<Guid, BoardFields> _fieldCache = new();

    public BoardSyncWorker(IOutboxRepository outbox, IProjectRepository projects,
        ITaskRepository tasks, IFindingRepository findings, IAttemptRepository attempts,
        IEventRepository events, ProjectsV2Client gh, ILogger<BoardSyncWorker> log)
    {
        _outbox = outbox; _projects = projects; _tasks = tasks;
        _findings = findings; _attempts = attempts; _events = events;
        _gh = gh; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await _outbox.ClaimBoardSyncBatchAsync(BatchSize, stoppingToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                foreach (var row in batch)
                    await ProcessAsync(row, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "board-sync worker loop failed; retrying after delay");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

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

            // Lazy create: if the task has no item id yet, create a draft item now and
            // persist the returned PVTI_ + databaseId before pushing the Status update.
            // If the task already has a node id but no number (legacy/backfill case), do
            // a lookup query — the slack adapter needs the number to build the pane URL.
            var itemId = task.GithubBoardItemId;
            if (string.IsNullOrEmpty(itemId))
            {
                var created = await _gh.CreateDraftItemAsync(
                    board.GithubProjectV2NodeId,
                    title: task.Title,
                    body: task.Description ?? "",
                    ct).ConfigureAwait(false);
                itemId = created.NodeId;
                await _tasks.SetGithubBoardItemIdAsync(task.Id, itemId, created.DatabaseId, ct).ConfigureAwait(false);
                _log.LogInformation("created draft item {ItemId} ({Number}) for task {TaskId}", itemId, created.DatabaseId, task.Id);
            }
            else
            {
                var existingNumber = await _tasks.GetGithubBoardItemNumberAsync(task.Id, ct).ConfigureAwait(false);
                if (existingNumber is null)
                {
                    var num = await _gh.LookupItemDatabaseIdAsync(itemId, ct).ConfigureAwait(false);
                    if (num is not null)
                    {
                        await _tasks.SetGithubBoardItemIdAsync(task.Id, itemId, num, ct).ConfigureAwait(false);
                        _log.LogInformation("backfilled databaseId {Number} for task {TaskId}", num, task.Id);
                    }
                }
            }

            await _gh.UpdateItemStatusFieldAsync(
                board.GithubProjectV2NodeId, itemId, board.StatusFieldNodeId, optionId, ct)
                .ConfigureAwait(false);

            // Assignment is best-effort: a missing GitHub user (e.g. a bot actor with a
            // fictional username) shouldn't fail the column flip we just succeeded at.
            // NULL  => don't touch assignees (default for create/transition flows)
            // ""    => clear all assignees (release_claim)
            // "x"   => assign to user x (claim, start_work)
            if (row.AssigneeGithubUsername is not null)
            {
                try
                {
                    if (row.AssigneeGithubUsername.Length == 0)
                    {
                        var draftId = await _gh.LookupDraftIssueIdAsync(itemId, ct).ConfigureAwait(false);
                        if (draftId is null)
                            _log.LogDebug("task {TaskId} item {ItemId} is not a draft; skipping clear-assignees", row.TaskId, itemId);
                        else
                            await _gh.UpdateDraftIssueAssigneesAsync(draftId, Array.Empty<string>(), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var (draftId, userId) = await _gh.LookupDraftAndUserAsync(itemId, row.AssigneeGithubUsername, ct).ConfigureAwait(false);
                        if (draftId is null)
                            _log.LogDebug("task {TaskId} item {ItemId} is not a draft issue; skipping assignee update", row.TaskId, itemId);
                        else if (userId is null)
                            _log.LogInformation("github user '{Login}' not found; leaving task {TaskId} unassigned", row.AssigneeGithubUsername, row.TaskId);
                        else
                            await _gh.UpdateDraftIssueAssigneesAsync(draftId, new[] { userId }, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception assignEx)
                {
                    _log.LogWarning(assignEx, "assignee update failed for task {TaskId}; column flip already succeeded", row.TaskId);
                }
            }

            // Best-effort: refresh the card's audit ledger body and Audit Stage field. A
            // failure here must not fail the column flip that already succeeded.
            try
            {
                await RefreshCardAsync(board, task, itemId, fields, ct).ConfigureAwait(false);
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
                var delay = TimeSpan.FromSeconds(Math.Pow(2, nextAttempt));
                _log.LogDebug(ex, "board sync row {Id} attempt {Attempt} failed; retrying in {Delay}",
                    row.Id, nextAttempt, delay);
                await _outbox.MarkBoardSyncResultAsync(row.Id, "retry", ex.Message, null, delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Re-render the task card's body as an audit ledger and, when the board exposes an
    /// "Audit Stage" field, set it. Only audit tasks (those with findings) or blocked /
    /// escalated tasks get a body rewrite — a plain task keeps its original description.
    /// </summary>
    private async Task RefreshCardAsync(ProjectBoard board, WorkTask task, string itemId, BoardFields fields, CancellationToken ct)
    {
        var findings = await _findings.ListByTaskAsync(task.Id, ct).ConfigureAwait(false);
        var isBlocked = task.Status is TaskStatus.Blocked or TaskStatus.NeedsHumanReview;
        if (findings.Count == 0 && !isBlocked)
            return;   // nothing audit-specific to surface — leave the description untouched

        // Closing commits for fixed findings (newest attempt with a commit hash wins).
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

        var draftId = await _gh.LookupDraftIssueIdAsync(itemId, ct).ConfigureAwait(false);
        if (draftId is not null)
            await _gh.UpdateDraftIssueBodyAsync(draftId, body, ct).ConfigureAwait(false);
        else
            _log.LogDebug("task {TaskId} item {ItemId} is not a draft; skipping ledger body", task.Id, itemId);

        // Optional "Audit Stage" single-select field, when the board has one.
        if (findings.Count > 0 && fields.AuditStageFieldId is { } stageField)
        {
            var stage = AuditLedger.ComputeStage(task.Status, findings);
            if (fields.AuditStageOptions.TryGetValue(stage, out var optId))
                await _gh.UpdateItemStatusFieldAsync(board.GithubProjectV2NodeId, itemId, stageField, optId, ct)
                    .ConfigureAwait(false);
            else
                _log.LogDebug("board {Board} has no Audit Stage option '{Stage}'; skipping", board.Id, stage);
        }
    }

    /// <summary>
    /// The reason a task was blocked / escalated, read from the most recent override
    /// event whose target state is blocked or needs_human_review.
    /// </summary>
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

    /// <summary>
    /// Resolve per-board field metadata once per process: the Blocked status option (so a
    /// "Blocked" column added on GitHub works without re-registering the board) and the
    /// optional "Audit Stage" single-select field.
    /// </summary>
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
                {
                    if (kv.Key.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
                    {
                        blockedOpt = kv.Value;
                        break;
                    }
                }
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
