using FluentAssertions;
using Workstream.Telemetry;
using Xunit;

namespace Workstream.UnitTests;

/// <summary>
/// Covers <see cref="TelemetryBootstrap.RedactTokenPath"/>: the URL token never reaches a
/// trace exporter, and paths that start with a known route are left alone.
/// </summary>
public sealed class TelemetryRedactionTests
{
    [Theory]
    [InlineData("/Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB/mcp", "/<token>/mcp")]
    [InlineData("/Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB", "/<token>")]
    [InlineData("/Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB/", "/<token>/")]
    public void RedactsTokenSegment(string path, string expected)
    {
        TelemetryBootstrap.RedactTokenPath(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("/admin/users")]
    [InlineData("/webhooks/github/projects")]
    [InlineData("/healthz")]
    [InlineData("/")]
    [InlineData("")]
    public void KeepsKnownRoutes(string path)
    {
        TelemetryBootstrap.RedactTokenPath(path).Should().Be(path);
    }

    [Fact]
    public void NullBecomesEmpty()
    {
        TelemetryBootstrap.RedactTokenPath(null).Should().BeEmpty();
    }
}
