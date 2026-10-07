using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Workstream.Mcp.Tools.Forensic;
using Xunit;

namespace Workstream.IntegrationTests.Tools;

/// <summary>
/// Covers <c>get_finding</c>: the read tool that lets a verifier or fixer recover the full
/// auditor-authored finding body when the claim tools only handed back id/status/severity.
/// Exercises the happy path and the structured not_found error per CLAUDE.md's tool-test rule.
/// </summary>
public sealed class GetFindingToolTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public GetFindingToolTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task GetFinding_ReturnsFullBody_ForKnownId()
    {
        var findings = new FindingRepository(_pg.ConnectionFactory);
        var seed = await SeedAuditTaskAsync().ConfigureAwait(false);

        var inserted = await findings.InsertManyAsync(seed.TaskId, new[]
        {
            new FindingInput("F-GF-1", "high", "compression-preserved",
                "the symptom", "the root cause", "1. repro step",
                "adversarial input", "expected X", "actual Y", "reference does Z"),
        }).ConfigureAwait(false);
        var findingId = inserted[0].Id;

        var tool = new GetFindingTool(findings);
        var result = await ((IMcpTool)tool).ExecuteAsync(
            new GetFindingInput(findingId), Ctx(seed.ActorId), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        var body = result.Data.Should().BeOfType<GetFindingOutput>().Subject;
        body.Id.Should().Be(findingId);
        body.TaskId.Should().Be(seed.TaskId);
        body.ExternalKey.Should().Be("F-GF-1");
        body.Severity.Should().Be("high");
        body.InvariantImpact.Should().Be("compression-preserved");
        body.Symptom.Should().Be("the symptom");
        body.RootCause.Should().Be("the root cause");
        body.ReproSteps.Should().Be("1. repro step");
        body.AdversarialInput.Should().Be("adversarial input");
        body.Expected.Should().Be("expected X");
        body.Actual.Should().Be("actual Y");
        body.ReferenceComparison.Should().Be("reference does Z");
        body.Status.Should().Be("pending_verification");
    }

    [Fact]
    public async Task GetFinding_ReturnsNotFound_ForUnknownId()
    {
        var findings = new FindingRepository(_pg.ConnectionFactory);
        var seed = await SeedAuditTaskAsync().ConfigureAwait(false);

        var tool = new GetFindingTool(findings);
        var result = await ((IMcpTool)tool).ExecuteAsync(
            new GetFindingInput(Guid.NewGuid()), Ctx(seed.ActorId), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetFinding_ExposesClaimHolderButNotToken_WhenFindingIsClaimed()
    {
        var findings = new FindingRepository(_pg.ConnectionFactory);
        var seed = await SeedAuditTaskAsync().ConfigureAwait(false);

        await findings.InsertManyAsync(seed.TaskId, new[]
        {
            new FindingInput("F-GF-2", "critical", "both",
                "sym", "rc", "repro", "adv", "exp", "act", "ref"),
        }).ConfigureAwait(false);

        // Claim it so the row carries a live claim token.
        var claimed = await findings.ClaimNextForVerificationAsync(
            seed.PlanId, seed.ActorId, TimeSpan.FromHours(1)).ConfigureAwait(false);
        claimed.Should().NotBeNull();

        var tool = new GetFindingTool(findings);
        var result = await ((IMcpTool)tool).ExecuteAsync(
            new GetFindingInput(claimed!.Finding.Id), Ctx(seed.ActorId), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        var body = result.Data.Should().BeOfType<GetFindingOutput>().Subject;
        // Claim metadata is visible so the caller knows the finding is held and by whom...
        body.ClaimActorId.Should().Be(seed.ActorId);
        body.ClaimRole.Should().Be("verifier");
        body.ClaimedUntil.Should().NotBeNull();
        // ...but the GetFindingOutput shape carries no claim-token field at all, so a plain
        // read can never be used to hijack someone else's claim.
        typeof(GetFindingOutput).GetProperty("ClaimToken").Should().BeNull();
    }

    private static RequestContext Ctx(Guid actorId) =>
        new(actorId, "human", "tester", false, false, false, false, "trace", DateTimeOffset.UtcNow);

    private async Task<(Guid PlanId, Guid TaskId, Guid ActorId)> SeedAuditTaskAsync()
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

        var actorId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO users (id, github_username, mcp_url_token, actor_type) VALUES (@id, @u, @t, 'human')",
            new { id = actorId, u = $"u{actorId:N}", t = $"tok-{actorId:N}" }).ConfigureAwait(false);

        var taskId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO tasks (id, plan_id, external_key, title) VALUES (@id, @planId, @key, 'T')",
            new { id = taskId, planId, key = "T-1" }).ConfigureAwait(false);

        return (planId, taskId, actorId);
    }
}
