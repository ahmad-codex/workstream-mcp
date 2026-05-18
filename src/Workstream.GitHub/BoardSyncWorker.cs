using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workstream.Data.Repositories;

namespace Workstream.GitHub;

/// <summary>
/// Background hosted service that drains <c>board_sync_log</c> (§7.5). One instance per API
/// replica. Exponential backoff on failure, capped at 5 attempts before the row is marked
/// <c>failed</c> and a <c>board_sync_failed</c> event is emitted.
///
/// Lazy item creation: the worker treats <c>tasks.github_board_item_id</c> as the source of
/// truth. If a task has never been synced, the worker calls <c>addProjectV2DraftIssue</c>
/// first, persists the returned <c>PVTI_…</c> back onto the task row, then updates the
/// Status field. This means tasks created before a project_board was registered also get
/// surfaced on the board on their first transition.
/// </summary>
public sealed class BoardSyncWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 16;
    private const int MaxAttempts = 5;

    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly ProjectsV2Client _gh;
    private readonly ILogger<BoardSyncWorker> _log;

    public BoardSyncWorker(IOutboxRepository outbox, IProjectRepository projects,
        ITaskRepository tasks, ProjectsV2Client gh, ILogger<BoardSyncWorker> log)
    {
        _outbox = outbox; _projects = projects; _tasks = tasks; _gh = gh; _log = log;
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

            var optionId = row.TargetColumn switch
            {
                "backlog"     => board.StatusOptionBacklog,
                "in_progress" => board.StatusOptionInProgress,
                "review"      => board.StatusOptionReview,
                "done"        => board.StatusOptionDone,
                "blocked"     => board.StatusOptionBlocked ?? board.StatusOptionBacklog,
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
}
