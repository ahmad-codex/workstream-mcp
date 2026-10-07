using System;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;
using Xunit;

namespace Workstream.IntegrationTests;

/// <summary>
/// End-to-end functional tests for the task lifecycle backing the §19.9 plug-and-play
/// acceptance criterion: claim → start_work → submit_attempt → reviewer-approval → done.
/// </summary>
public sealed class TaskLifecycleTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public TaskLifecycleTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task DevTask_FullLifecycle_FromPendingToDone()
    {
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var (planId, _, actorIds) = await SeedAsync(taskCount: 1, actorCount: 1).ConfigureAwait(false);
        var developer = actorIds[0];

        // claim
        var claim = await tasks.ClaimNextAsync(planId, developer, "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);
        claim.Should().NotBeNull();
        claim!.Task.Status.Should().Be("claimed");

        // start_work
        var inProgress = await tasks.UpdateStatusWithClaimAsync(
            claim.ClaimToken, developer, "in_progress",
            clearClaim: false, eventType: "started_work", eventPayloadJson: null).ConfigureAwait(false);
        inProgress!.Status.Should().Be("in_progress");

        // submit_attempt → review (clears the claim)
        var review = await tasks.UpdateStatusWithClaimAsync(
            claim.ClaimToken, developer, "review",
            clearClaim: true, eventType: "attempt_submitted", eventPayloadJson: null).ConfigureAwait(false);
        review!.Status.Should().Be("review");
        review.Claim.IsActive.Should().BeFalse();

        // reviewer approves via override (the actual SubmitReviewDecisionTool path)
        var done = await tasks.OverrideStatusAsync(claim.Task.Id, developer, "done", "approved").ConfigureAwait(false);
        done!.Status.Should().Be("done");

        // Verify the event log captured every transition.
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var events = (await conn.QueryAsync<string>(
            "SELECT event_type FROM events WHERE entity_type='task' AND entity_id=@id ORDER BY at",
            new { id = claim.Task.Id }).ConfigureAwait(false));
        events.Should().Contain(new[] { "created", "claimed", "started_work", "attempt_submitted", "override" });
    }

    [Fact]
    public async Task RefreshClaim_ExtendsClaimedUntil_AndEmitsEvent()
    {
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var (planId, _, actorIds) = await SeedAsync(taskCount: 1, actorCount: 1).ConfigureAwait(false);
        var developer = actorIds[0];

        // Claim with a short TTL so we can observe the extension as a clear delta.
        var claim = await tasks.ClaimNextAsync(planId, developer, "developer", TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        claim.Should().NotBeNull();
        var initialUntil = claim!.Task.Claim.ClaimedUntil!.Value;

        var refreshed = await tasks.RefreshClaimAsync(claim.ClaimToken, developer, TimeSpan.FromHours(2)).ConfigureAwait(false);
        refreshed.Should().NotBeNull();
        refreshed!.Claim.ClaimedUntil.Should().NotBeNull();
        // The new claimed_until is now + 2h, which is meaningfully greater than the 5m baseline.
        (refreshed.Claim.ClaimedUntil!.Value - initialUntil).Should().BeGreaterThan(TimeSpan.FromMinutes(30));

        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var events = (await conn.QueryAsync<string>(
            "SELECT event_type FROM events WHERE entity_type='task' AND entity_id=@id ORDER BY at",
            new { id = claim.Task.Id }).ConfigureAwait(false));
        events.Should().Contain("claim_refreshed");
    }

    [Fact]
    public async Task RefreshClaim_ReturnsNull_WhenTokenIsUnknown()
    {
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var (_, _, actorIds) = await SeedAsync(taskCount: 0, actorCount: 1).ConfigureAwait(false);
        var who = actorIds[0];

        var result = await tasks.RefreshClaimAsync(Guid.NewGuid(), who, TimeSpan.FromHours(1)).ConfigureAwait(false);
        result.Should().BeNull();
    }

    [Fact]
    public async Task RefreshClaim_ReturnsNull_WhenCalledByDifferentActor()
    {
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var (planId, _, actorIds) = await SeedAsync(taskCount: 1, actorCount: 2).ConfigureAwait(false);
        var holder = actorIds[0];
        var imposter = actorIds[1];

        var claim = await tasks.ClaimNextAsync(planId, holder, "developer", TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        claim.Should().NotBeNull();

        // A different actor cannot extend someone else's claim.
        var attempt = await tasks.RefreshClaimAsync(claim!.ClaimToken, imposter, TimeSpan.FromHours(2)).ConfigureAwait(false);
        attempt.Should().BeNull();
    }

    [Fact]
    public async Task OverrideFinding_SetsStatus_AndEmitsOverrideEvent()
    {
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var findings = new FindingRepository(_pg.ConnectionFactory);
        var (planId, taskIds, actorIds) = await SeedAuditAsync(taskCount: 1, actorCount: 1).ConfigureAwait(false);
        var auditor = actorIds[0];
        var taskId = taskIds[0];

        // Insert one finding so we have something to override.
        var inserted = await findings.InsertManyAsync(taskId, new[]
        {
            new FindingInput("F-X-1", "high", "invariant-preserved", "sym", "rc", "repro", "adv", "exp", "act", "reference"),
        }).ConfigureAwait(false);
        var findingId = inserted[0].Id;

        var overridden = await findings.OverrideStatusAsync(findingId, auditor, "confirmed", "verifier finished after TTL expired").ConfigureAwait(false);
        overridden.Should().NotBeNull();
        overridden!.Status.Should().Be("confirmed");

        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var events = (await conn.QueryAsync<(string EventType, string FromState, string ToState)>(
            "SELECT event_type AS EventType, from_state AS FromState, to_state AS ToState FROM events WHERE entity_type='finding' AND entity_id=@id ORDER BY at",
            new { id = findingId }).ConfigureAwait(false));
        events.Should().ContainSingle(e => e.EventType == "override" && e.FromState == "pending_verification" && e.ToState == "confirmed");
    }

    private async Task<(Guid PlanId, System.Collections.Generic.IReadOnlyList<Guid> TaskIds, System.Collections.Generic.IReadOnlyList<Guid> ActorIds)> SeedAuditAsync(int taskCount, int actorCount)
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var projectId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO projects (id, slug, display_name) VALUES (@id, @slug, 'P')",
            new { id = projectId, slug = $"p-{projectId:N}" }).ConfigureAwait(false);

        var planId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO plans (id, project_id, plan_type_id, name, status) VALUES (@id, @projectId, 'audit', 'P', 'active')",
            new { id = planId, projectId }).ConfigureAwait(false);

        var actors = new System.Collections.Generic.List<Guid>();
        for (int i = 0; i < actorCount; i++)
        {
            var aid = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO users (id, github_username, mcp_url_token, actor_type) VALUES (@id, @u, @t, 'human')",
                new { id = aid, u = $"u{aid:N}", t = $"tok-{aid:N}" }).ConfigureAwait(false);
            actors.Add(aid);
        }

        var taskIds = new System.Collections.Generic.List<Guid>();
        for (int i = 0; i < taskCount; i++)
        {
            var tid = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO tasks (id, plan_id, external_key, title) VALUES (@id, @planId, @key, 'T')",
                new { id = tid, planId, key = $"T-{i + 1}" }).ConfigureAwait(false);
            taskIds.Add(tid);
        }
        return (planId, taskIds, actors);
    }

    private async Task<(Guid PlanId, System.Collections.Generic.IReadOnlyList<Guid> TaskIds, System.Collections.Generic.IReadOnlyList<Guid> ActorIds)> SeedAsync(int taskCount, int actorCount)
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var projectId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO projects (id, slug, display_name) VALUES (@id, @slug, 'P')",
            new { id = projectId, slug = $"p-{projectId:N}" }).ConfigureAwait(false);

        var planId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO plans (id, project_id, plan_type_id, name, status) VALUES (@id, @projectId, 'development', 'P', 'active')",
            new { id = planId, projectId }).ConfigureAwait(false);

        var actors = new System.Collections.Generic.List<Guid>();
        for (int i = 0; i < actorCount; i++)
        {
            var aid = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO users (id, github_username, mcp_url_token, actor_type) VALUES (@id, @u, @t, 'human')",
                new { id = aid, u = $"u{aid:N}", t = $"tok-{aid:N}" }).ConfigureAwait(false);
            actors.Add(aid);
        }

        var taskIds = new System.Collections.Generic.List<Guid>();
        for (int i = 0; i < taskCount; i++)
        {
            var tid = Guid.NewGuid();
            await conn.ExecuteAsync(
                "INSERT INTO tasks (id, plan_id, external_key, title) VALUES (@id, @planId, @key, 'T')",
                new { id = tid, planId, key = $"T-{i + 1}" }).ConfigureAwait(false);
            taskIds.Add(tid);
        }
        return (planId, taskIds, actors);
    }
}
