using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Core.Domain;
using Workstream.Core.StateMachine;
using Workstream.Data;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Workstream.Mcp.Notifications;
using Workstream.Mcp.Tools.Submission;
using Xunit;

namespace Workstream.IntegrationTests.Tools;

/// <summary>
/// Covers <c>submit_review_decision</c> with <c>changes_requested</c> on a development plan.
/// The task must go back to pending so a developer can claim it again (issue #70), and the
/// retry cap must still send it to needs_human_review. Uses the plan type as migrated, so the
/// test also checks migration 0021.
/// </summary>
public sealed class SubmitReviewDecisionToolTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public SubmitReviewDecisionToolTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task ChangesRequested_ReturnsTaskToPending_AndItCanBeClaimedAgain()
    {
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var (planId, developer, reviewer) = await SeedAsync().ConfigureAwait(false);

        var first = await SubmitAttemptAsync(tasks, planId, developer).ConfigureAwait(false);

        var result = await ((IMcpTool)NewTool(tasks)).ExecuteAsync(
            new SubmitReviewDecisionInput(first, "changes_requested", "Add a test"),
            Ctx(reviewer), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeTrue(result.Error?.Message);
        result.Data.Should().BeOfType<SubmitReviewDecisionOutput>().Which.Status.Should().Be("pending");

        var again = await tasks.ClaimNextAsync(planId, developer, "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);
        again.Should().NotBeNull();
        again!.Task.Id.Should().Be(first);
    }

    [Fact]
    public async Task ChangesRequested_AtRetryCap_MovesToNeedsHumanReview()
    {
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var (planId, developer, reviewer) = await SeedAsync().ConfigureAwait(false);
        var tool = NewTool(tasks);

        // The development profile's retry cap is 3: two rounds go back to pending, the third
        // attempt is escalated.
        Guid taskId = default;
        for (var round = 1; round <= 3; round++)
        {
            taskId = await SubmitAttemptAsync(tasks, planId, developer).ConfigureAwait(false);
            var result = await ((IMcpTool)tool).ExecuteAsync(
                new SubmitReviewDecisionInput(taskId, "changes_requested"), Ctx(reviewer), CancellationToken.None).ConfigureAwait(false);
            result.Ok.Should().BeTrue(result.Error?.Message);
            result.Data.Should().BeOfType<SubmitReviewDecisionOutput>().Which.Status
                .Should().Be(round < 3 ? "pending" : "needs_human_review");
        }
    }

    /// <summary>Claim the next task, start it and submit an attempt, as a developer would.</summary>
    private async Task<Guid> SubmitAttemptAsync(TaskRepository tasks, Guid planId, Guid developer)
    {
        var claim = await tasks.ClaimNextAsync(planId, developer, "developer", TimeSpan.FromHours(1)).ConfigureAwait(false);
        claim.Should().NotBeNull();
        await tasks.UpdateStatusWithClaimAsync(claim!.ClaimToken, developer, "in_progress",
            clearClaim: false, eventType: "started_work", eventPayloadJson: null).ConfigureAwait(false);
        await new AttemptRepository(_pg.ConnectionFactory).InsertForTaskAsync(claim.Task.Id, developer,
            new AttemptInput(new[] { "a.cs" }, "change", null, null, null, null)).ConfigureAwait(false);
        await tasks.UpdateStatusWithClaimAsync(claim.ClaimToken, developer, "review",
            clearClaim: true, eventType: "attempt_submitted", eventPayloadJson: null).ConfigureAwait(false);
        return claim.Task.Id;
    }

    private SubmitReviewDecisionTool NewTool(TaskRepository tasks)
    {
        var planTypes = new PostgresPlanTypeCache(new PlanTypeRepository(_pg.ConnectionFactory));
        return new SubmitReviewDecisionTool(tasks, new AttemptRepository(_pg.ConnectionFactory),
            new PlanRepository(_pg.ConnectionFactory), planTypes, new StateMachineService(planTypes),
            new EventRepository(_pg.ConnectionFactory), new NullBoardSync(), new NullSlackNotify());
    }

    private static RequestContext Ctx(Guid actorId) =>
        new(actorId, "subagent", "reviewer", false, false, false, true, "trace", DateTimeOffset.UtcNow);

    private async Task<(Guid PlanId, Guid Developer, Guid Reviewer)> SeedAsync()
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var projectId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var developer = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO projects (id, slug, display_name) VALUES (@projectId, @slug, 'Review');
            INSERT INTO plans (id, project_id, plan_type_id, name, status) VALUES (@planId, @projectId, 'development', 'Review', 'active');
            INSERT INTO users (id, github_username, mcp_url_token, actor_type) VALUES
                (@developer, @devName, @devToken, 'subagent'),
                (@reviewer, @revName, @revToken, 'subagent');
            INSERT INTO tasks (plan_id, external_key, title) VALUES (@planId, 'R-1', 'Rework me');
            """, new
        {
            projectId,
            planId,
            developer,
            reviewer,
            slug = $"review-{projectId:N}",
            devName = $"dev-{developer:N}",
            devToken = $"tok-{developer:N}",
            revName = $"rev-{reviewer:N}",
            revToken = $"tok-{reviewer:N}",
        }).ConfigureAwait(false);
        return (planId, developer, reviewer);
    }

    private sealed class NullSlackNotify : ISlackNotifyEnqueue
    {
        public Task EnqueueForTaskAsync(Plan plan, (PlanType Row, StateGraph Graph) planType, WorkTask task,
            string notificationType, RequestContext ctx, IReadOnlyDictionary<string, string>? extraTokens = null,
            IOutboxScope? scope = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task EnqueueForFindingAsync(Plan plan, (PlanType Row, StateGraph Graph) planType, Finding finding,
            Guid taskId, string notificationType, RequestContext ctx, IReadOnlyDictionary<string, string>? extraTokens = null,
            IOutboxScope? scope = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task EnqueueForPlanAsync(Plan plan, (PlanType Row, StateGraph Graph) planType,
            string notificationType, RequestContext ctx, IReadOnlyDictionary<string, string>? extraTokens = null,
            IOutboxScope? scope = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullBoardSync : IBoardSyncEnqueue
    {
        public Task EnqueueAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus,
            string? assigneeGithubUsername = null, IOutboxScope? scope = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
