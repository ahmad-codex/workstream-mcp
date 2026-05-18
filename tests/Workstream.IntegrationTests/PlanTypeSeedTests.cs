using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Workstream.Core.StateMachine;
using Workstream.Data;
using Workstream.Data.Repositories;
using Xunit;

namespace Workstream.IntegrationTests;

public sealed class PlanTypeSeedTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public PlanTypeSeedTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task BothSeedProfilesLoadAndParse()
    {
        var repo = new PlanTypeRepository(_pg.ConnectionFactory);
        var list = await repo.ListAsync();
        list.Select(p => p.Id).Should().Contain(new[] { "audit", "development" });

        var audit = list.First(p => p.Id == "audit");
        audit.RequiresFindings.Should().BeTrue();
        audit.RetryCap.Should().Be(3);
        var graph = StateGraphParser.Parse(audit.StateGraphJson);
        graph.TaskStates.Should().Contain("in_progress");
        graph.FindingStates.Should().Contain("in_fix");

        var dev = list.First(p => p.Id == "development");
        dev.RequiresFindings.Should().BeFalse();
        var devGraph = StateGraphParser.Parse(dev.StateGraphJson);
        devGraph.FindingStates.Should().BeEmpty();
    }
}
