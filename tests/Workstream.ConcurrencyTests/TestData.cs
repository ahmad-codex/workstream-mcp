using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Workstream.Data;

namespace Workstream.ConcurrencyTests;

/// <summary>
/// Helpers for seeding a test plan with N tasks and a roster of actors. Used by every
/// concurrency test.
/// </summary>
public static class TestData
{
    public static async Task<(Guid PlanId, IReadOnlyList<Guid> TaskIds, IReadOnlyList<Guid> ActorIds)> SeedAsync(
        IDbConnectionFactory factory, int taskCount, int actorCount, string planType = "development")
    {
        await using var conn = await factory.OpenAsync().ConfigureAwait(false);

        // Project
        var projectId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO projects (id, slug, display_name) VALUES (@id, @slug, @name)
            """, new { id = projectId, slug = $"test-{projectId:N}", name = "Test Project" }).ConfigureAwait(false);

        // Plan
        var planId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO plans (id, project_id, plan_type_id, name, status)
            VALUES (@id, @projectId, @ptype, 'Test Plan', 'active')
            """, new { id = planId, projectId, ptype = planType }).ConfigureAwait(false);

        // Actors
        var actorIds = new List<Guid>(actorCount);
        for (var i = 0; i < actorCount; i++)
        {
            var aid = Guid.NewGuid();
            await conn.ExecuteAsync("""
                INSERT INTO users (id, github_username, mcp_url_token, actor_type, display_name)
                VALUES (@id, @user, @tok, 'orchestrator', @disp)
                """, new
                {
                    id = aid,
                    user = $"actor-{aid:N}",
                    tok = $"tok-{aid:N}",
                    disp = $"Actor {i}",
                }).ConfigureAwait(false);
            actorIds.Add(aid);
        }

        // Tasks
        var taskIds = new List<Guid>(taskCount);
        for (var i = 0; i < taskCount; i++)
        {
            var tid = Guid.NewGuid();
            await conn.ExecuteAsync("""
                INSERT INTO tasks (id, plan_id, external_key, title, priority, status)
                VALUES (@id, @planId, @key, @title, @prio, 'pending')
                """, new
                {
                    id = tid,
                    planId,
                    key = $"T-{i + 1:D3}",
                    title = $"Test task {i + 1}",
                    prio = i % 5,
                }).ConfigureAwait(false);
            taskIds.Add(tid);
        }

        return (planId, taskIds, actorIds);
    }
}
