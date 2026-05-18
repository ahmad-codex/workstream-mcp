using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workstream.Data.Repositories;

namespace Workstream.GitHub;

/// <summary>
/// Background hosted service that drains <c>board_sync_log</c> (§7.5). One instance per API
/// replica acquires a Postgres advisory lock so only one drains at a time. Exponential
/// backoff on failure, capped at 5 attempts before the row is marked <c>failed</c> and a
/// <c>board_sync_failed</c> event is emitted.
/// </summary>
public sealed class BoardSyncWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 16;
    private const int MaxAttempts = 5;

    private readonly IOutboxRepository _outbox;
    private readonly IProjectRepository _projects;
    private readonly ProjectsV2Client _gh;
    private readonly ILogger<BoardSyncWorker> _log;

    public BoardSyncWorker(IOutboxRepository outbox, IProjectRepository projects,
        ProjectsV2Client gh, ILogger<BoardSyncWorker> log)
    {
        _outbox = outbox; _projects = projects; _gh = gh; _log = log;
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
                {
                    await ProcessAsync(row, stoppingToken).ConfigureAwait(false);
                }
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
            var optionId = row.TargetColumn switch
            {
                "backlog"     => board.StatusOptionBacklog,
                "in_progress" => board.StatusOptionInProgress,
                "review"      => board.StatusOptionReview,
                "done"        => board.StatusOptionDone,
                "blocked"     => board.StatusOptionBlocked ?? board.StatusOptionBacklog,
                _             => board.StatusOptionBacklog,
            };
            // For brand-new tasks the board item id might not exist yet. v1: we assume
            // the activate_plan path created items; if not, we surface a failed row and let
            // operators retry. Future: auto-create draft item here.
            await _gh.UpdateItemStatusFieldAsync(board.GithubProjectV2NodeId,
                itemNodeId: row.TaskId.ToString()!,    // placeholder: real impl resolves from tasks.github_board_item_id
                statusFieldNodeId: board.StatusFieldNodeId,
                optionId: optionId, ct).ConfigureAwait(false);
            await _outbox.MarkBoardSyncResultAsync(row.Id, "success", null, null, null, ct).ConfigureAwait(false);
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
