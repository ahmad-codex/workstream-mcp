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
/// Covers <c>request_audit</c>: resolves each project name and enqueues a row on the
/// <c>audit_dispatch_log</c> outbox. Exercises the happy path, slug-vs-id resolution,
/// the unknown-project result, and the admin gate — per CLAUDE.md's tool-test rule.
/// </summary>
public sealed class RequestAuditToolTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public RequestAuditToolTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task RequestAudit_QueuesKnownProjects_AndFlagsUnknownOnes()
    {
        var slug = await SeedProjectAsync().ConfigureAwait(false);
        var tool = NewTool();

        var result = await ((IMcpTool)tool).ExecuteAsync(
            new RequestAuditInput(new[] { slug, "no-such-project" }),
            Ctx(isAdmin: true), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        var body = result.Data.Should().BeOfType<RequestAuditOutput>().Subject;
        body.Results.Should().HaveCount(2);

        var queued = body.Results.Should().ContainSingle(r => r.Project == slug).Subject;
        queued.Status.Should().Be("queued");
        queued.DispatchId.Should().NotBeNull();

        body.Results.Should().ContainSingle(r => r.Project == "no-such-project")
            .Which.Status.Should().Be("unknown_project");

        // Exactly one outbox row landed, in the pending state the worker claims.
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_dispatch_log WHERE id=@id AND status='pending'",
            new { id = queued.DispatchId }).ConfigureAwait(false);
        count.Should().Be(1);
    }

    [Fact]
    public async Task RequestAudit_ResolvesProjectById()
    {
        var slug = await SeedProjectAsync().ConfigureAwait(false);
        var projects = new ProjectRepository(_pg.ConnectionFactory);
        var project = await projects.GetBySlugAsync(slug).ConfigureAwait(false);

        var result = await ((IMcpTool)NewTool()).ExecuteAsync(
            new RequestAuditInput(new[] { project!.Id.ToString() }),
            Ctx(isAdmin: true), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeTrue();
        var body = result.Data.Should().BeOfType<RequestAuditOutput>().Subject;
        body.Results.Should().ContainSingle()
            .Which.Status.Should().Be("queued");
    }

    [Fact]
    public async Task RequestAudit_ReturnsPermissionDenied_ForNonAdmin()
    {
        var slug = await SeedProjectAsync().ConfigureAwait(false);

        var result = await ((IMcpTool)NewTool()).ExecuteAsync(
            new RequestAuditInput(new[] { slug }),
            Ctx(isAdmin: false), CancellationToken.None).ConfigureAwait(false);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be(ErrorCodes.PermissionDenied);

        // Nothing was enqueued when the gate rejected the call.
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_dispatch_log").ConfigureAwait(false);
        count.Should().Be(0);
    }

    private RequestAuditTool NewTool() =>
        new(new ProjectRepository(_pg.ConnectionFactory), new OutboxRepository(_pg.ConnectionFactory));

    private static RequestContext Ctx(bool isAdmin) =>
        new(Guid.NewGuid(), "human", "tester", isAdmin, false, false, false, "trace", DateTimeOffset.UtcNow);

    private async Task<string> SeedProjectAsync()
    {
        await using var conn = await _pg.ConnectionFactory.OpenAsync().ConfigureAwait(false);
        var id = Guid.NewGuid();
        var slug = $"p-{id:N}";
        await conn.ExecuteAsync(
            "INSERT INTO projects (id, slug, display_name) VALUES (@id, @slug, 'P')",
            new { id, slug }).ConfigureAwait(false);
        return slug;
    }
}
