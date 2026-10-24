using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Api;
using Workstream.Mcp;
using Xunit;

namespace Workstream.UnitTests;

/// <summary>
/// Pins the MCP annotations of the real tool set, built the way discovery mode builds it, so a
/// tool can't silently change from a read to a change or lose its destructive flag.
/// </summary>
public sealed class McpToolHintsTests
{
    private static Dictionary<string, IMcpTool> Tools()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkstreamData("Host=unconfigured");
        services.AddWorkstreamMcpTools();
        return services.BuildServiceProvider().GetServices<IMcpTool>().ToDictionary(t => t.Name);
    }

    [Fact]
    public void ReadsAreTheOnlyReadOnlyTools()
    {
        var reads = Tools().Values.Where(t => t.Hints.ReadOnly).Select(t => t.Name);

        reads.Should().BeEquivalentTo(
            "list_projects", "list_plans", "get_plan_dashboard", "get_my_active_work",
            "get_event_log", "get_stuck_work", "export_plan", "get_finding");
    }

    [Fact]
    public void ToolsThatDiscardStateOrBypassTheFlowAreDestructive()
    {
        var destructive = Tools().Values.Where(t => t.Hints.Destructive).Select(t => t.Name);

        destructive.Should().BeEquivalentTo(
            "mark_task_status", "override_verdict", "set_project_slack", "archive_plan", "cancel_audit");
    }

    [Fact]
    public void ClaimsAndSubmissionsAreNotIdempotent()
    {
        var tools = Tools();

        foreach (var name in new[] { "claim_next_task", "claim_specific_task", "submit_attempt", "submit_findings", "create_task" })
            tools[name].Hints.Should().Be(ToolHints.Additive, name);
    }

    [Fact]
    public void OnlyRequestAuditReachesOutsideTheServer()
    {
        Tools().Values.Where(t => t.Hints.OpenWorld).Select(t => t.Name).Should().Equal("request_audit");
    }
}
