using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Workstream.Api.Demo;
using Workstream.Api.Mcp;
using Workstream.Api.Middleware;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;
using Workstream.Mcp;
using Xunit;

namespace Workstream.UnitTests;

/// <summary>
/// Covers the opt-in modes: demo mode is off unless WORKSTREAM_DEMO=true, the public demo
/// token is refused outside demo mode, stdio is only used when asked for, and discovery mode
/// (no database) lists tools but refuses to call them.
/// </summary>
public sealed class DemoAndStdioModeTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    public void DemoModeIsOptIn(string? value, bool expected)
    {
        var cfg = value is null ? Config() : Config((DemoMode.EnvVar, value));
        DemoMode.IsEnabled(cfg).Should().Be(expected);
    }

    [Fact]
    public void DemoTokenIsRefusedOutsideDemoMode()
    {
        DemoMode.IsTokenAllowed(DemoMode.Token, demoEnabled: false).Should().BeFalse();
        DemoMode.IsTokenAllowed(DemoMode.Token, demoEnabled: true).Should().BeTrue();
    }

    [Fact]
    public void OtherTokensAreUnaffected()
    {
        DemoMode.IsTokenAllowed("Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB", demoEnabled: false).Should().BeTrue();
        DemoMode.IsTokenAllowed(DemoMode.Token.ToUpperInvariant(), demoEnabled: false).Should().BeTrue();
    }

    [Fact]
    public async Task MiddlewareReturns404ForDemoTokenWhenDemoIsOff()
    {
        var (status, lookups) = await InvokeMiddlewareAsync($"/{DemoMode.Token}/mcp", demoEnabled: false);

        status.Should().Be(404);
        lookups.Should().BeEmpty("the demo token must be rejected before any database lookup");
    }

    [Fact]
    public async Task MiddlewareLooksUpDemoTokenWhenDemoIsOn()
    {
        var (_, lookups) = await InvokeMiddlewareAsync($"/{DemoMode.Token}/mcp", demoEnabled: true);

        lookups.Should().Contain(nameof(IUserRepository.ResolveByTokenAsync));
    }

    [Fact]
    public async Task MiddlewareStillLooksUpOrdinaryTokensWhenDemoIsOff()
    {
        var (_, lookups) = await InvokeMiddlewareAsync("/some-real-token/mcp", demoEnabled: false);

        lookups.Should().Contain(nameof(IUserRepository.ResolveByTokenAsync));
    }

    [Theory]
    [InlineData(new[] { "--stdio" }, null, true)]
    [InlineData(new string[0], "stdio", true)]
    [InlineData(new string[0], "STDIO", true)]
    [InlineData(new string[0], null, false)]
    [InlineData(new string[0], "http", false)]
    public void StdioIsOptIn(string[] args, string? transport, bool expected)
    {
        var cfg = transport is null ? Config() : Config(("WORKSTREAM_TRANSPORT", transport));
        StdioServer.IsRequested(args, cfg).Should().Be(expected);
    }

    [Fact]
    public async Task DiscoveryModeListsToolsAndRefusesCalls()
    {
        var probe = new ProbeTool();
        var tools = new Dictionary<string, IMcpTool> { ["probe"] = probe };
        var input = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"probe","arguments":{}}}""");
        var output = new StringWriter();

        await StdioServer.ServeAsync(new StringReader(input), output, ctx: null, tools, CancellationToken.None);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        lines.Should().HaveCount(3, "the notification gets no response");
        lines[0].GetProperty("result").GetProperty("protocolVersion").GetString().Should().Be(McpProtocol.ProtocolVersion);
        lines[1].GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString().Should().Be("probe");
        var call = lines[2].GetProperty("result");
        call.GetProperty("isError").GetBoolean().Should().BeTrue();
        call.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain(McpProtocol.NotConfiguredCode);
        probe.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ConfiguredModeCallsTheTool()
    {
        var probe = new ProbeTool();
        var tools = new Dictionary<string, IMcpTool> { ["probe"] = probe };
        var ctx = new RequestContext(Guid.NewGuid(), "human", "alice", false, false, false, false, "t", DateTimeOffset.UtcNow);

        var response = await McpProtocol.HandleAsync(
            new McpProtocol.JsonRpcRequest("2.0", 1, "tools/call",
                JsonDocument.Parse("""{"name":"probe","arguments":{}}""").RootElement),
            ctx, tools, CancellationToken.None);

        JsonSerializer.Serialize(response, McpProtocol.JsonOpts).Should().Contain("\"isError\":false");
        probe.Calls.Should().Be(1);
    }

    [Fact]
    public async Task PingReturnsEmptyResult()
    {
        var response = await McpProtocol.HandleAsync(
            new McpProtocol.JsonRpcRequest("2.0", 7, "ping", null), ctx: null,
            new Dictionary<string, IMcpTool>(), CancellationToken.None);

        JsonSerializer.Serialize(response, McpProtocol.JsonOpts).Should().Be("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}");
    }

    private static async Task<(int Status, List<string> Lookups)> InvokeMiddlewareAsync(string path, bool demoEnabled)
    {
        var lookups = new List<string>();
        var users = RecordingProxy<IUserRepository>.Create(lookups);
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        var middleware = new TokenResolutionMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(http, users,
            new TokenResolutionMiddleware.Options { AdminToken = "admin", DemoEnabled = demoEnabled });

        return (http.Response.StatusCode, lookups);
    }

    public sealed record ProbeInput(string? Note = null);

    private sealed class ProbeTool : McpTool<ProbeInput, object>
    {
        public int Calls;
        public override string Name => "probe";
        public override string Description => "test tool";
        protected override Task<object> RunAsync(ProbeInput input, RequestContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<object>(new { ok = true });
        }
    }

    /// <summary>Records method names and returns default results (null users, false flags).</summary>
    public class RecordingProxy<T> : DispatchProxy
    {
        private List<string> _calls = null!;

        public static T Create(List<string> calls)
        {
            var proxy = DispatchProxy.Create<T, RecordingProxy<T>>();
            ((RecordingProxy<T>)(object)proxy!)._calls = calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            _calls.Add(method!.Name);
            var rt = method.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
            }
            return null;
        }
    }
}
