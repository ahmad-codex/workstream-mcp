using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Workstream.Mcp.Tools.Setup;
using Xunit;

namespace Workstream.IntegrationTests.Tools;

/// <summary>
/// Covers <c>create_plan</c>'s board-inheritance behaviour. A plan only gets board sync
/// when it carries a <c>primary_board_id</c>; orchestrators call create_plan without one,
/// so the tool inherits the project's board when the project has exactly one registered.
/// This is the fix for "bulk tasks never appear on the GitHub board" — per CLAUDE.md's
/// tool-test rule.
/// </summary>
public sealed class CreatePlanToolTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public CreatePlanToolTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task CreatePlan_InheritsProjectBoard_WhenProjectHasExactlyOne()
    {
        var (projectId, actorId) = await SeedProjectAsync().ConfigureAwait(false);
        var boardId = await AddBoardAsync(projectId, "Board A").ConfigureAwait(false);

        var result = await RunAsync(new CreatePlanInput(projectId, "audit", "Audit pass"), actorId).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        var body = result.Data.Should().BeOfType<CreatePlanOutput>().Subject;
        body.PrimaryBoardId.Should().Be(boardId, "a single-board project must auto-bind so board sync fires");
    }

    [Fact]
    public async Task CreatePlan_LeavesBoardUnbound_WhenProjectHasNoBoard()
    {
        var (projectId, actorId) = await SeedProjectAsync().ConfigureAwait(false);

        var result = await RunAsync(new CreatePlanInput(projectId, "audit", "Audit pass"), actorId).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        result.Data.Should().BeOfType<CreatePlanOutput>().Subject.PrimaryBoardId.Should().BeNull();
    }

    [Fact]
    public async Task CreatePlan_DoesNotGuess_WhenProjectHasMultipleBoards()
    {
        var (projectId, actorId) = await SeedProjectAsync().ConfigureAwait(false);
        await AddBoardAsync(projectId, "Board A").ConfigureAwait(false);
        await AddBoardAsync(projectId, "Board B").ConfigureAwait(false);

        var result = await RunAsync(new CreatePlanInput(projectId, "audit", "Audit pass"), actorId).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        result.Data.Should().BeOfType<CreatePlanOutput>().Subject.PrimaryBoardId
            .Should().BeNull("with more than one board the caller must name one explicitly");
    }

    [Fact]
    public async Task CreatePlan_HonoursExplicitBoardId_OverInheritance()
    {
        var (projectId, actorId) = await SeedProjectAsync().ConfigureAwait(false);
        var inherited = await AddBoardAsync(projectId, "Board A").ConfigureAwait(false);
        var chosen = await AddBoardAsync(projectId, "Board B").ConfigureAwait(false);

        var result = await RunAsync(
            new CreatePlanInput(projectId, "audit", "Audit pass", PrimaryBoardId: chosen), actorId).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        result.Data.Should().BeOfType<CreatePlanOutput>().Subject.PrimaryBoardId.Should().Be(chosen);
    }

    [Fact]
    public async Task CreatePlan_ReturnsPermissionDenied_ForNonAdmin()
    {
        var (projectId, actorId) = await SeedProjectAsync().ConfigureAwait(false);

        var tool = new CreatePlanTool(new PlanRepository(_pg.ConnectionFactory), new ProjectRepository(_pg.ConnectionFactory));
        var result = await ((IMcpTool)tool).ExecuteAsync(
            new CreatePlanInput(projectId, "audit", "Audit pass"),
            Ctx(actorId, isAdmin: false), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCodes.PermissionDenied);
    }

    private async Task<ToolResult> RunAsync(CreatePlanInput input, Guid actorId)
    {
        var tool = new CreatePlanTool(new PlanRepository(_pg.ConnectionFactory), new ProjectRepository(_pg.ConnectionFactory));
        return await ((IMcpTool)tool).ExecuteAsync(input, Ctx(actorId, isAdmin: true), CancellationToken.None).ConfigureAwait(false);
    }

    private static RequestContext Ctx(Guid actorId, bool isAdmin) =>
        new(actorId, "human", "tester", isAdmin, false, false, false, "trace", DateTimeOffset.UtcNow);

    private async Task<(Guid ProjectId, Guid ActorId)> SeedProjectAsync()
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);

        var projectId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO projects (id, slug, display_name) VALUES (@id, @slug, 'P')",
            new { id = projectId, slug = $"p-{projectId:N}" }).ConfigureAwait(false);

        var actorId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO users (id, github_username, mcp_url_token, actor_type) VALUES (@id, @u, @t, 'human')",
            new { id = actorId, u = $"u{actorId:N}", t = $"tok-{actorId:N}" }).ConfigureAwait(false);

        return (projectId, actorId);
    }

    private async Task<Guid> AddBoardAsync(Guid projectId, string displayName)
    {
        var repo = new ProjectRepository(_pg.ConnectionFactory);
        return await repo.AddBoardAsync(new ProjectBoard
        {
            ProjectId              = projectId,
            GithubProjectV2NodeId  = $"PVT_{Guid.NewGuid():N}",
            GithubProjectNumber    = Random.Shared.Next(1, 100_000),
            GithubOwner            = "test-org",
            DisplayName            = displayName,
            StatusFieldNodeId      = $"PVTSSF_{Guid.NewGuid():N}",
            StatusOptionBacklog    = "backlog",
            StatusOptionInProgress = "in_progress",
            StatusOptionReview     = "review",
            StatusOptionDone       = "done",
            CreatedAt              = DateTimeOffset.UtcNow,
        }).ConfigureAwait(false);
    }
}
