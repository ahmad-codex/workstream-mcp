using System;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
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
