using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Workstream.Mcp.Tools.Forensic;
using Xunit;

namespace Workstream.IntegrationTests.Tools;

/// <summary>
/// Covers <c>export_plan</c> with <c>format: json</c>: the export carries tasks, findings and
/// attempts, but never a claim token (issue #71).
/// </summary>
public sealed class ExportPlanToolTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public ExportPlanToolTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task JsonExport_LeavesOutClaimTokens()
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var projectId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var taskToken = Guid.NewGuid();
        var findingToken = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO projects (id, slug, display_name) VALUES (@projectId, @slug, 'Export');
            INSERT INTO plans (id, project_id, plan_type_id, name, status) VALUES (@planId, @projectId, 'audit', 'Export', 'active');
            INSERT INTO users (id, github_username, mcp_url_token, actor_type) VALUES (@actorId, @username, @token, 'subagent');
            INSERT INTO tasks (id, plan_id, external_key, title, status, claim_actor_id, claim_role, claim_token, claimed_at, claimed_until)
            VALUES (@taskId, @planId, 'EX-1', 'Claimed task', 'in_progress', @actorId, 'auditor', @taskToken, now(), now() + interval '1 hour');
            INSERT INTO findings (task_id, external_key, status, claim_actor_id, claim_role, claim_token, claimed_at, claimed_until)
            VALUES (@taskId, 'F-EX-1', 'pending_verification', @actorId, 'verifier', @findingToken, now(), now() + interval '1 hour');
            """, new
        {
            projectId,
            planId,
            taskId,
            actorId,
            taskToken,
            findingToken,
            slug = $"export-{projectId:N}",
            username = $"export-{actorId:N}",
            token = $"tok-{actorId:N}",
        }).ConfigureAwait(false);

        var tool = new ExportPlanTool(new PlanRepository(_pg.ConnectionFactory), new TaskRepository(_pg.ConnectionFactory), _pg.ConnectionFactory);
        var result = await ((IMcpTool)tool).ExecuteAsync(new ExportPlanInput(planId, "json"),
            new RequestContext(actorId, "subagent", "tester", false, false, false, false, "trace", DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeTrue(result.Error?.Message);
        var json = result.Data.Should().BeOfType<ExportPlanOutput>().Subject.Content;
        json.Should().Contain("EX-1").And.Contain("F-EX-1");
        json.Should().NotContain("claim_token");
        json.Should().NotContain(taskToken.ToString()).And.NotContain(findingToken.ToString());
    }
}
