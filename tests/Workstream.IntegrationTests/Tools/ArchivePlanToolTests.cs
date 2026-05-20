using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Core.StateMachine;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Workstream.Mcp.Notifications;
using Workstream.Mcp.Tools.Claims;
using Workstream.Mcp.Tools.Setup;
using Xunit;

namespace Workstream.IntegrationTests.Tools;

/// <summary>
/// Covers <c>archive_plan</c>: the tool that disables a plan so it stops accepting work.
/// Exercises the happy path, the structured-error cases (permission_denied, not_found),
/// idempotency, and the <c>plan_archived</c> guard that stops an orchestrator resuming or
/// re-activating a disabled plan — per CLAUDE.md's tool-test rule.
/// </summary>
public sealed class ArchivePlanToolTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public ArchivePlanToolTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task ArchivePlan_SetsStatusArchived_AndEmitsEvent()
    {
        var plans = new PlanRepository(_pg.ConnectionFactory);
        var seed = await SeedDevPlanAsync(taskCount: 0).ConfigureAwait(false);

        var tool = NewArchiveTool(plans);
        var result = await ((IMcpTool)tool).ExecuteAsync(
            new ArchivePlanInput(seed.PlanId, "starting a fresh pass"),
            Ctx(seed.ActorId, canArchivePlan: true), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        var body = result.Data.Should().BeOfType<ArchivePlanOutput>().Subject;
        body.Id.Should().Be(seed.PlanId);
        body.Status.Should().Be("archived");
        body.AlreadyArchived.Should().BeFalse();

        // The status really changed in the DB...
        var reloaded = await plans.GetAsync(seed.PlanId).ConfigureAwait(false);
        reloaded!.Status.Should().Be("archived");

        // ...and the append-only events log captured the archive with from/to states.
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var events = await conn.QueryAsync<(string EventType, string FromState, string ToState)>(
            "SELECT event_type AS EventType, from_state AS FromState, to_state AS ToState " +
            "FROM events WHERE entity_type='plan' AND entity_id=@id",
            new { id = seed.PlanId }).ConfigureAwait(false);
        events.Should().ContainSingle(e =>
            e.EventType == "archived" && e.FromState == "active" && e.ToState == "archived");
    }

    [Fact]
    public async Task ArchivePlan_IsIdempotent_WhenAlreadyArchived()
    {
        var plans = new PlanRepository(_pg.ConnectionFactory);
        var seed = await SeedDevPlanAsync(taskCount: 0).ConfigureAwait(false);
        var tool = NewArchiveTool(plans);
        var ctx = Ctx(seed.ActorId, canArchivePlan: true);

        var first = await ((IMcpTool)tool).ExecuteAsync(
            new ArchivePlanInput(seed.PlanId), ctx, CancellationToken.None).ConfigureAwait(false);
        first.Ok.Should().BeTrue();

        var second = await ((IMcpTool)tool).ExecuteAsync(
            new ArchivePlanInput(seed.PlanId), ctx, CancellationToken.None).ConfigureAwait(false);
        second.Ok.Should().BeTrue();
        var body = second.Data.Should().BeOfType<ArchivePlanOutput>().Subject;
        body.AlreadyArchived.Should().BeTrue();
        body.Status.Should().Be("archived");

        // The no-op second call must NOT write a second archived event.
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM events WHERE entity_type='plan' AND entity_id=@id AND event_type='archived'",
            new { id = seed.PlanId }).ConfigureAwait(false);
        count.Should().Be(1);
    }

    [Fact]
    public async Task ArchivePlan_ReturnsPermissionDenied_WithoutCanArchivePlan()
    {
        var plans = new PlanRepository(_pg.ConnectionFactory);
        var seed = await SeedDevPlanAsync(taskCount: 0).ConfigureAwait(false);

        var tool = NewArchiveTool(plans);
        var result = await ((IMcpTool)tool).ExecuteAsync(
            new ArchivePlanInput(seed.PlanId),
            Ctx(seed.ActorId, canArchivePlan: false), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCodes.PermissionDenied);

        // The plan is untouched when the permission gate rejects the call.
        var reloaded = await plans.GetAsync(seed.PlanId).ConfigureAwait(false);
        reloaded!.Status.Should().Be("active");
    }

    [Fact]
    public async Task ArchivePlan_ReturnsNotFound_ForUnknownPlan()
    {
        var plans = new PlanRepository(_pg.ConnectionFactory);
        var seed = await SeedDevPlanAsync(taskCount: 0).ConfigureAwait(false);

        var tool = NewArchiveTool(plans);
        var result = await ((IMcpTool)tool).ExecuteAsync(
            new ArchivePlanInput(Guid.NewGuid()),
            Ctx(seed.ActorId, canArchivePlan: true), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ClaimNextTask_OnArchivedPlan_ReturnsPlanArchived()
    {
        var plans = new PlanRepository(_pg.ConnectionFactory);
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var seed = await SeedDevPlanAsync(taskCount: 1).ConfigureAwait(false);

        // Archive the plan first.
        var archived = await ((IMcpTool)NewArchiveTool(plans)).ExecuteAsync(
            new ArchivePlanInput(seed.PlanId),
            Ctx(seed.ActorId, canArchivePlan: true), CancellationToken.None).ConfigureAwait(false);
        archived.Ok.Should().BeTrue();

        // An orchestrator that tries to resume it must be turned away with plan_archived.
        var claim = new ClaimNextTaskTool(tasks, plans, new NullPlanTypeCache(), new NullBoardSync());
        var result = await ((IMcpTool)claim).ExecuteAsync(
            new ClaimNextTaskInput(seed.PlanId, "developer"), Ctx(seed.ActorId), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCodes.PlanArchived);

        // The task is still pending — nothing was claimed on the disabled plan.
        var planTasks = await tasks.ListByPlanAsync(seed.PlanId).ConfigureAwait(false);
        planTasks[0].Status.Should().Be("pending");
    }

    [Fact]
    public async Task ActivatePlan_OnArchivedPlan_ReturnsPlanArchived()
    {
        var plans = new PlanRepository(_pg.ConnectionFactory);
        var tasks = new TaskRepository(_pg.ConnectionFactory);
        var seed = await SeedDevPlanAsync(taskCount: 0).ConfigureAwait(false);

        var archived = await ((IMcpTool)NewArchiveTool(plans)).ExecuteAsync(
            new ArchivePlanInput(seed.PlanId),
            Ctx(seed.ActorId, canArchivePlan: true), CancellationToken.None).ConfigureAwait(false);
        archived.Ok.Should().BeTrue();

        // Archiving is one-way: activate_plan must not resurrect a disabled plan.
        var activate = new ActivatePlanTool(plans, tasks, new NullPlanTypeCache(), new NullBoardSync(), new NullSlackNotify());
        var result = await ((IMcpTool)activate).ExecuteAsync(
            new ActivatePlanInput(seed.PlanId), Ctx(seed.ActorId, isAdmin: true), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCodes.PlanArchived);

        var reloaded = await plans.GetAsync(seed.PlanId).ConfigureAwait(false);
        reloaded!.Status.Should().Be("archived");
    }

    private ArchivePlanTool NewArchiveTool(PlanRepository plans) =>
        new(plans, new NullPlanTypeCache(), new EventRepository(_pg.ConnectionFactory), new NullSlackNotify());

    private static RequestContext Ctx(Guid actorId, bool canArchivePlan = false, bool isAdmin = false) =>
        // (actorId, actorType, githubUsername, isAdmin, canOverrideVerdict, canArchivePlan, canMarkNeedsHumanReview, traceId, now)
        new(actorId, "human", "tester", isAdmin, false, canArchivePlan, false, "trace", DateTimeOffset.UtcNow);

    private async Task<(Guid PlanId, Guid ActorId)> SeedDevPlanAsync(int taskCount)
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

        var actorId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO users (id, github_username, mcp_url_token, actor_type) VALUES (@id, @u, @t, 'human')",
            new { id = actorId, u = $"u{actorId:N}", t = $"tok-{actorId:N}" }).ConfigureAwait(false);

        for (var i = 0; i < taskCount; i++)
        {
            await conn.ExecuteAsync(
                "INSERT INTO tasks (id, plan_id, external_key, title) VALUES (@id, @planId, @key, 'T')",
                new { id = Guid.NewGuid(), planId, key = $"T-{i + 1}" }).ConfigureAwait(false);
        }
        return (planId, actorId);
    }

    // --- Minimal test doubles. archive_plan's Slack post and the claim/activate tools' board
    //     sync + plan-type lookup are not under test here (the seeded plan has no Slack or
    //     board configured), so these no-op stand-ins keep the tests focused on plan state. ---

    private sealed class NullSlackNotify : ISlackNotifyEnqueue
    {
        public Task EnqueueForTaskAsync(Plan plan, (PlanType Row, StateGraph Graph) planType, WorkTask task,
            string notificationType, RequestContext ctx, IReadOnlyDictionary<string, string>? extraTokens = null,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task EnqueueForFindingAsync(Plan plan, (PlanType Row, StateGraph Graph) planType, Finding finding,
            Guid taskId, string notificationType, RequestContext ctx, IReadOnlyDictionary<string, string>? extraTokens = null,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task EnqueueForPlanAsync(Plan plan, (PlanType Row, StateGraph Graph) planType,
            string notificationType, RequestContext ctx, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullBoardSync : IBoardSyncEnqueue
    {
        public Task EnqueueAsync(Guid taskId, Guid boardId, string targetColumn, string targetStatus,
            string? assigneeGithubUsername = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullPlanTypeCache : IPlanTypeCache
    {
        public Task<(PlanType Row, StateGraph Graph)?> GetAsync(string planTypeId, CancellationToken ct = default)
            => Task.FromResult<(PlanType Row, StateGraph Graph)?>(null);

        public void Invalidate(string planTypeId) { }
    }
}
