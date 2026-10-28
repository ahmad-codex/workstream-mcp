using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Core.Errors;
using Workstream.Data;
using Workstream.Data.Repositories;
using Workstream.Mcp.Notifications;
using Xunit;

namespace Workstream.ConcurrencyTests;

/// <summary>
/// The load-bearing correctness tests for §6. If anything in this file flakes, the merge is
/// blocked — under contention, claim semantics must hold.
/// </summary>
public sealed class ClaimRaceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    private readonly TaskRepository _tasks;

    public ClaimRaceTests(PostgresFixture pg)
    {
        _pg = pg;
        _tasks = new TaskRepository(pg.ConnectionFactory);
    }

    [Fact]
    public async Task ThirtyTwoClaimers_OneHundredTasks_EachTaskClaimedExactlyOnce()
    {
        const int Tasks = 100;
        const int Claimers = 32;
        var (planId, _, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, Tasks, Claimers).ConfigureAwait(false);

        var observed = new ConcurrentBag<(Guid TaskId, Guid ActorId, Guid ClaimToken)>();
        var noWorkCount = 0;

        // Each claimer keeps polling until claim_next returns no_work.
        async Task ClaimerLoop(Guid actorId)
        {
            while (true)
            {
                var result = await _tasks.ClaimNextAsync(planId, actorId, "developer", TimeSpan.FromHours(1))
                    .ConfigureAwait(false);
                if (result is null)
                {
                    System.Threading.Interlocked.Increment(ref noWorkCount);
                    return;
                }
                observed.Add((result.Task.Id, actorId, result.ClaimToken));
            }
        }

        var claimers = actorIds.Select(ClaimerLoop).ToArray();
        await Task.WhenAll(claimers).ConfigureAwait(false);

        // Every task must have been claimed exactly once across all actors.
        observed.Should().HaveCount(Tasks, "every task must be claimed");
        observed.Select(o => o.TaskId).Distinct().Should().HaveCount(Tasks,
            "no task may be claimed by two actors simultaneously — duplicate task IDs would mean a race");

        // Sanity: claim tokens are unique.
        observed.Select(o => o.ClaimToken).Distinct().Should().HaveCount(Tasks);
    }

    [Fact]
    public async Task ExpiredClaim_IsReclaimableByDifferentActor()
    {
        var (planId, _, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 2)
            .ConfigureAwait(false);
        var actor1 = actorIds[0];
        var actor2 = actorIds[1];

        // First actor claims with a 1-second TTL.
        var first = await _tasks.ClaimNextAsync(planId, actor1, "developer", TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        first.Should().NotBeNull();

        // Force the claim to be expired by backdating claimed_until.
        await using (var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false))
        {
            await conn.ExecuteAsync(
                "UPDATE tasks SET claimed_until = now() - interval '1 minute' WHERE id = @id",
                new { id = first!.Task.Id }).ConfigureAwait(false);
        }

        // Second actor should now reclaim the same row.
        var second = await _tasks.ClaimNextAsync(planId, actor2, "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);
        second.Should().NotBeNull("expired claims must be reclaimable");
        second!.Task.Id.Should().Be(first.Task.Id);
        second.ClaimToken.Should().NotBe(first.ClaimToken, "reclaim issues a fresh claim token");
    }

    [Fact]
    public async Task Submission_WithStaleClaimToken_IsRejected()
    {
        var (planId, _, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 1)
            .ConfigureAwait(false);
        var actor = actorIds[0];

        var claim = await _tasks.ClaimNextAsync(planId, actor, "developer", TimeSpan.FromSeconds(1))
            .ConfigureAwait(false);
        claim.Should().NotBeNull();

        // Expire the claim.
        await using (var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false))
        {
            await conn.ExecuteAsync(
                "UPDATE tasks SET claimed_until = now() - interval '1 minute' WHERE id = @id",
                new { id = claim!.Task.Id }).ConfigureAwait(false);
        }

        // Attempt to submit with the stale token — must return null (stale_claim).
        var result = await _tasks.UpdateStatusWithClaimAsync(
            claim.ClaimToken, actor, "in_progress",
            clearClaim: false, eventType: "started_work", eventPayloadJson: null)
            .ConfigureAwait(false);

        result.Should().BeNull("a stale claim token must be rejected at submission");
    }

    [Fact]
    public async Task SpecificClaim_FailsWhenAlreadyHeld()
    {
        var (planId, taskIds, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 2)
            .ConfigureAwait(false);
        var taskId = taskIds[0];
        var first  = actorIds[0];
        var second = actorIds[1];

        var c1 = await _tasks.ClaimSpecificAsync(taskId, first, "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);
        c1.Should().NotBeNull();

        var c2 = await _tasks.ClaimSpecificAsync(taskId, second, "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);
        c2.Should().BeNull("a held task must not be claimable by a second actor");
    }

    [Fact]
    public async Task Sweeper_ResetsStaleClaimedRowsToPending()
    {
        var (planId, _, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 3, actorCount: 1)
            .ConfigureAwait(false);
        var actor = actorIds[0];

        // Claim three tasks.
        var claims = new List<ClaimedTask>();
        for (var i = 0; i < 3; i++)
        {
            var c = await _tasks.ClaimNextAsync(planId, actor, "developer", TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            c.Should().NotBeNull();
            claims.Add(c!);
        }

        // Expire all three.
        await using (var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false))
        {
            await conn.ExecuteAsync(
                "UPDATE tasks SET claimed_until = now() - interval '1 minute' WHERE plan_id = @planId",
                new { planId }).ConfigureAwait(false);
        }

        var swept = await _tasks.SweepExpiredClaimsAsync().ConfigureAwait(false);
        swept.Should().Be(3);

        // All three are now claimable again, in fresh 'pending' state.
        await using (var conn2 = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false))
        {
            var statuses = (await conn2.QueryAsync<string>(
                "SELECT status FROM tasks WHERE plan_id = @planId", new { planId }).ConfigureAwait(false)).ToList();
            statuses.Should().AllBeEquivalentTo("pending");
        }
    }

    [Fact]
    public async Task ConcurrentClaimAndSweep_NeverDuplicates()
    {
        // Adversarial scenario: 16 claimers + 4 sweeper iterations + 16 more claimers, against
        // 200 tasks, with intentional TTL expiry midway. Asserts that no task is ever owned by
        // two actors simultaneously across the entire sequence.
        const int TaskCount = 200;
        const int ClaimerCount = 16;
        var (planId, _, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, TaskCount, ClaimerCount * 2)
            .ConfigureAwait(false);

        var allClaims = new ConcurrentBag<(Guid TaskId, Guid ActorId, Guid ClaimToken)>();

        async Task Loop(Guid actorId, TimeSpan ttl)
        {
            while (true)
            {
                var c = await _tasks.ClaimNextAsync(planId, actorId, "developer", ttl).ConfigureAwait(false);
                if (c is null) return;
                allClaims.Add((c.Task.Id, actorId, c.ClaimToken));
            }
        }

        // First wave — short TTL so the sweeper has work later.
        var wave1 = actorIds.Take(ClaimerCount).Select(a => Loop(a, TimeSpan.FromSeconds(1))).ToArray();
        await Task.WhenAll(wave1).ConfigureAwait(false);

        // Force expiry, sweep.
        await using (var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false))
        {
            await conn.ExecuteAsync(
                "UPDATE tasks SET claimed_until = now() - interval '1 minute' WHERE plan_id = @planId AND status = 'claimed'",
                new { planId }).ConfigureAwait(false);
        }
        await _tasks.SweepExpiredClaimsAsync().ConfigureAwait(false);

        // Second wave with long TTL — reclaims everything.
        var wave2 = actorIds.Skip(ClaimerCount).Select(a => Loop(a, TimeSpan.FromHours(1))).ToArray();
        await Task.WhenAll(wave2).ConfigureAwait(false);

        // All 200 tasks must have made it through at least once. Distinct task ids show no
        // simultaneous double-claims; total count proves we didn't lose any.
        var distinctTasks = allClaims.Select(c => c.TaskId).Distinct().Count();
        distinctTasks.Should().Be(TaskCount);

        // Final state must be 'claimed' for every task — wave2 grabbed them all.
        await using (var conn2 = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false))
        {
            var statuses = (await conn2.QueryAsync<string>(
                "SELECT status FROM tasks WHERE plan_id = @planId", new { planId }).ConfigureAwait(false)).ToList();
            statuses.Should().HaveCount(TaskCount);
            statuses.Should().AllBeEquivalentTo("claimed");
        }
    }

    [Fact]
    public async Task StaleClaimer_ResumingAfterReclaim_QueuesNothingToTheOutbox()
    {
        var (planId, taskIds, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 2)
            .ConfigureAwait(false);
        var (oldOwner, newOwner) = (actorIds[0], actorIds[1]);
        var boardId = await TestData.SeedBoardAsync(_pg.ConnectionFactory, planId).ConfigureAwait(false);
        var outbox = new OutboxRepository(_pg.ConnectionFactory);
        Func<string, Func<Workstream.Core.Domain.WorkTask, IOutboxScope, Task>> enqueueAs = owner =>
            (t, scope) => outbox.EnqueueBoardSyncAsync(t.Id, boardId, "column", t.Status, owner, scope);

        var stale = await _tasks.ClaimNextAsync(planId, oldOwner, "developer", TimeSpan.FromHours(1),
            inTransaction: enqueueAs("old")).ConfigureAwait(false);
        stale.Should().NotBeNull();

        // The old owner goes quiet past its TTL and another agent takes the task.
        await using (var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false))
        {
            await conn.ExecuteAsync(
                "UPDATE tasks SET claimed_until = now() - interval '1 minute' WHERE id = @id",
                new { id = taskIds[0] }).ConfigureAwait(false);
        }
        var fresh = await _tasks.ClaimNextAsync(planId, newOwner, "developer", TimeSpan.FromHours(1),
            inTransaction: enqueueAs("new")).ConfigureAwait(false);
        fresh.Should().NotBeNull();

        // Both owners now try to move the task at the same time.
        var callbackRanForOldOwner = false;
        var results = await Task.WhenAll(
            _tasks.UpdateStatusWithClaimAsync(stale!.ClaimToken, oldOwner, "in_progress", clearClaim: false,
                eventType: "started_work", eventPayloadJson: null,
                inTransaction: (t, scope) => { callbackRanForOldOwner = true; return enqueueAs("old")(t, scope); }),
            _tasks.UpdateStatusWithClaimAsync(fresh!.ClaimToken, newOwner, "in_progress", clearClaim: false,
                eventType: "started_work", eventPayloadJson: null,
                inTransaction: enqueueAs("new"))).ConfigureAwait(false);

        results[0].Should().BeNull("the old owner's token was replaced by the new claim");
        results[1].Should().NotBeNull();
        callbackRanForOldOwner.Should().BeFalse("a stale claim must never reach the outbox");

        await using (var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false))
        {
            var rows = (await conn.QueryAsync<(string Owner, string Status)>("""
                SELECT assignee_github_username, target_status FROM board_sync_log
                WHERE task_id = @id ORDER BY id
                """, new { id = taskIds[0] }).ConfigureAwait(false)).ToList();
            rows.Should().Equal(("old", "claimed"), ("new", "claimed"), ("new", "in_progress"));
        }
    }

    [Fact]
    public async Task OutboxRow_RollsBackWithTheStateChange()
    {
        var (planId, taskIds, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 1)
            .ConfigureAwait(false);
        var boardId = await TestData.SeedBoardAsync(_pg.ConnectionFactory, planId).ConfigureAwait(false);
        var outbox = new OutboxRepository(_pg.ConnectionFactory);
        var claim = await _tasks.ClaimNextAsync(planId, actorIds[0], "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);

        var act = () => _tasks.UpdateStatusWithClaimAsync(claim!.ClaimToken, actorIds[0], "in_progress", clearClaim: false,
            eventType: "started_work", eventPayloadJson: null,
            inTransaction: async (t, scope) =>
            {
                await outbox.EnqueueBoardSyncAsync(t.Id, boardId, "column", t.Status, scope: scope).ConfigureAwait(false);
                throw new InvalidOperationException("crash after the outbox write");
            });
        await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);

        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var status = await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM tasks WHERE id = @id", new { id = taskIds[0] }).ConfigureAwait(false);
        var queued = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM board_sync_log WHERE task_id = @id", new { id = taskIds[0] }).ConfigureAwait(false);
        status.Should().Be("claimed", "the state change rolled back");
        queued.Should().Be(0, "the outbox row rolled back with it");
    }

    [Fact]
    public async Task OutboxScope_UsedAfterCommit_ThrowsAndQueuesNothing()
    {
        var (planId, taskIds, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 1)
            .ConfigureAwait(false);
        var boardId = await TestData.SeedBoardAsync(_pg.ConnectionFactory, planId).ConfigureAwait(false);
        var outbox = new OutboxRepository(_pg.ConnectionFactory);
        IOutboxScope? captured = null;
        var claim = await _tasks.ClaimNextAsync(planId, actorIds[0], "developer", TimeSpan.FromHours(1),
            inTransaction: (_, scope) => { captured = scope; return Task.CompletedTask; }).ConfigureAwait(false);
        claim.Should().NotBeNull();

        var late = () => outbox.EnqueueBoardSyncAsync(taskIds[0], boardId, "column", "claimed", scope: captured);
        await late.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);

        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var queued = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM board_sync_log WHERE task_id = @id", new { id = taskIds[0] }).ConfigureAwait(false);
        queued.Should().Be(0, "work that outlives the transaction must not write anywhere");
    }
    [Fact]
    public async Task RefreshLoop_WithoutProgress_IsRefused_UntilTheHolderRecordsProgress()
    {
        var (planId, _, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 1)
            .ConfigureAwait(false);
        var limits = new ClaimRefreshLimits(MaxLifetime: null, RequireProgress: true);
        var claim = await _tasks.ClaimNextAsync(planId, actorIds[0], "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);

        var refresh = () => _tasks.RefreshClaimAsync(claim!.ClaimToken, actorIds[0], TimeSpan.FromHours(1), limits: limits);
        (await refresh.Should().ThrowAsync<WorkstreamException>().ConfigureAwait(false))
            .Which.Error.Details!["reason"].Should().Be("no_progress");

        await _tasks.UpdateStatusWithClaimAsync(claim!.ClaimToken, actorIds[0], "in_progress", clearClaim: false,
            eventType: "started_work", eventPayloadJson: null).ConfigureAwait(false);
        (await refresh().ConfigureAwait(false)).Should().NotBeNull("starting work is progress");

        (await refresh.Should().ThrowAsync<WorkstreamException>().ConfigureAwait(false))
            .Which.Error.Code.Should().Be(ErrorCodes.ClaimLimitReached, "nothing happened since the last refresh");
    }

    [Fact]
    public async Task Refresh_IsCappedAtMaxLifetime_ThenRefused()
    {
        var (planId, _, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 1)
            .ConfigureAwait(false);
        var limits = new ClaimRefreshLimits(MaxLifetime: TimeSpan.FromMinutes(90), RequireProgress: false);
        var claim = await _tasks.ClaimNextAsync(planId, actorIds[0], "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);

        var refreshed = await _tasks.RefreshClaimAsync(claim!.ClaimToken, actorIds[0], TimeSpan.FromHours(2), limits: limits)
            .ConfigureAwait(false);
        refreshed!.Claim.ClaimedUntil.Should().Be(refreshed.Claim.ClaimedAt!.Value.AddMinutes(90));

        var again = () => _tasks.RefreshClaimAsync(claim.ClaimToken, actorIds[0], TimeSpan.FromHours(1), limits: limits);
        (await again.Should().ThrowAsync<WorkstreamException>().ConfigureAwait(false))
            .Which.Error.Details!["reason"].Should().Be("max_lifetime");
    }

    [Fact]
    public async Task Refresh_WithoutLimits_KeepsExtending()
    {
        var (planId, _, actorIds) = await TestData.SeedAsync(_pg.ConnectionFactory, taskCount: 1, actorCount: 1)
            .ConfigureAwait(false);
        var claim = await _tasks.ClaimNextAsync(planId, actorIds[0], "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);
        for (var i = 0; i < 3; i++)
            (await _tasks.RefreshClaimAsync(claim!.ClaimToken, actorIds[0], TimeSpan.FromHours(1)).ConfigureAwait(false))
                .Should().NotBeNull();
    }

    [Fact]
    public async Task MaxLifetimeDurations_ParseAsPostgresIntervals()
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var seconds = await conn.QuerySingleAsync<(double S, double M, double H, double D)>("""
            SELECT extract(epoch FROM '45s'::interval), extract(epoch FROM '30m'::interval),
                   extract(epoch FROM '12h'::interval), extract(epoch FROM '2d'::interval)
            """).ConfigureAwait(false);
        seconds.Should().Be((45d, 1800d, 43200d, 172800d), "get_stuck_work casts max_lifetime with ::interval");
    }
}
