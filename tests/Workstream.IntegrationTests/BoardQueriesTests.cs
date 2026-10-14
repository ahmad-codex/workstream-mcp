using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Workstream.Data.Repositories;
using Xunit;

namespace Workstream.IntegrationTests;

public sealed class BoardQueriesTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public BoardQueriesTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task ListTasksAsync_ReturnsClaimHolderPhaseAndDependencies()
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var projectId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var phaseId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await conn.ExecuteAsync("""
            INSERT INTO projects (id, slug, display_name) VALUES (@projectId, @slug, 'Board');
            INSERT INTO plans (id, project_id, plan_type_id, name, status) VALUES (@planId, @projectId, 'development', 'Board', 'active');
            INSERT INTO phases (id, plan_id, order_index, name) VALUES (@phaseId, @planId, 1, 'API');
            INSERT INTO users (id, github_username, display_name, mcp_url_token, actor_type)
            VALUES (@actorId, @username, 'Codex', @token, 'subagent');
            INSERT INTO tasks (id, plan_id, phase_id, external_key, title, priority, status, claim_actor_id, claim_role, claim_token, claimed_at, claimed_until)
            VALUES (@firstId, @planId, @phaseId, 'B-1', 'First', 3, 'in_progress', @actorId, 'developer', gen_random_uuid(), now(), now() + interval '1 hour');
            INSERT INTO tasks (id, plan_id, external_key, title, priority, status)
            VALUES (@secondId, @planId, 'B-2', 'Second', 1, 'pending');
            INSERT INTO task_dependencies (task_id, depends_on_task_id) VALUES (@secondId, @firstId);
            INSERT INTO findings (task_id, external_key, severity, status) VALUES (@firstId, 'F-1', 'high', 'confirmed');
            """, new
        {
            projectId,
            planId,
            phaseId,
            firstId,
            secondId,
            actorId,
            slug = $"board-{projectId:N}",
            username = $"board-{actorId:N}",
            token = $"token-{actorId:N}",
        }).ConfigureAwait(false);

        var board = new BoardQueries(_pg.ConnectionFactory);
        var tasks = await board.ListTasksAsync(planId).ConfigureAwait(false);
        var findings = await board.ListFindingsAsync(planId).ConfigureAwait(false);
        var names = await board.GetActorNamesAsync(new[] { actorId, Guid.Empty }).ConfigureAwait(false);

        tasks.Select(t => t.ExternalKey).Should().Equal("B-1", "B-2");
        var first = tasks[0];
        first.Phase.Should().Be("API");
        first.ClaimActor.Should().Be("Codex");
        first.ClaimRole.Should().Be("developer");
        first.ClaimedUntil.Should().NotBeNull();
        tasks[1].DependsOn.Should().Equal(firstId);
        tasks[1].ClaimActor.Should().BeNull();

        findings.Should().ContainSingle().Which.Severity.Should().Be("high");
        names.Should().ContainKey(actorId).WhoseValue.Should().Be("Codex");
        names.Should().HaveCount(1);
    }
}
