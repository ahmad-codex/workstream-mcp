using System;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Workstream.Data.StuckWork;
using Xunit;

namespace Workstream.IntegrationTests;

public sealed class StuckWorkJobTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public StuckWorkJobTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task RunOnceAsync_WritesCountsForExpiredTaskClaimAndStaleFinding()
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var projectId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO projects (id, slug, display_name) VALUES (@projectId, @slug, 'Stuck work');
            INSERT INTO plans (id, project_id, plan_type_id, name, status) VALUES (@planId, @projectId, 'audit', 'Stuck work', 'active');
            INSERT INTO users (id, github_username, mcp_url_token, actor_type)
            VALUES (@actorId, @username, @token, 'subagent');
            INSERT INTO tasks (id, plan_id, external_key, title, status, claim_actor_id, claim_role, claim_token, claimed_at, claimed_until)
            VALUES (@taskId, @planId, 'STUCK-1', 'Expired task', 'in_progress', @actorId, 'developer', gen_random_uuid(), now() - interval '2 hours', now() - interval '1 hour');
            INSERT INTO findings (task_id, external_key, status, created_at)
            VALUES (@taskId, 'F-STUCK-1', 'pending_verification', now() - interval '25 hours');
            """, new
        {
            projectId,
            planId,
            taskId,
            actorId,
            slug = $"stuck-{projectId:N}",
            username = $"stuck-{actorId:N}",
            token = $"token-{actorId:N}",
        }).ConfigureAwait(false);

        var job = new StuckWorkJob(_pg.ConnectionFactory, NullLogger<StuckWorkJob>.Instance);
        await job.RunOnceAsync(default).ConfigureAwait(false);

        var summary = await conn.QuerySingleAsync<string>("SELECT summary::text FROM stuck_work_reports ORDER BY id DESC LIMIT 1").ConfigureAwait(false);
        summary.Should().Contain("\"stuck_tasks\": 1");
        summary.Should().Contain("\"stuck_findings\": 1");
    }
}
