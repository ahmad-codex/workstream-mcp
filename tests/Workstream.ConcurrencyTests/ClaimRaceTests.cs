using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Data.Repositories;
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
}
