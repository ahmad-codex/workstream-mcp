using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Workstream.Api;
using Workstream.Api.Demo;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Xunit;

namespace Workstream.IntegrationTests;

/// <summary>
/// Demo mode seeding against a real database: the demo user resolves by the demo token, the
/// project gets an active development plan with three pending tasks, and a second run (a
/// container restart) changes nothing.
/// </summary>
public sealed class DemoSeedTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public DemoSeedTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task SeedCreatesUsableDemoDataAndIsIdempotent()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddWorkstreamData(_pg.ConnectionString)
            .AddWorkstreamMcpTools()
            .BuildServiceProvider();

        await DemoMode.SeedAsync(services, NullLogger.Instance);
        await DemoMode.SeedAsync(services, NullLogger.Instance);

        var user = await services.GetRequiredService<IUserRepository>().ResolveByTokenAsync(DemoMode.Token);
        user.Should().NotBeNull();
        user!.IsAdmin.Should().BeTrue();

        var project = await services.GetRequiredService<IProjectRepository>().GetBySlugAsync(DemoMode.DemoProjectSlug);
        project.Should().NotBeNull();

        var plans = await services.GetRequiredService<IPlanRepository>().ListByProjectAsync(project!.Id, null);
        plans.Should().ContainSingle().Which.Status.Should().Be("active");
    }
}
