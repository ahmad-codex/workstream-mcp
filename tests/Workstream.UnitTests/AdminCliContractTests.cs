using System.Text.Json;
using FluentAssertions;
using Workstream.Admin;
using Workstream.Api.Endpoints;
using Xunit;

namespace Workstream.UnitTests;

/// <summary>
/// The admin CLI's request bodies must bind to the server's request records. Both sides use
/// System.Text.Json web defaults (camelCase, case-insensitive), the same as
/// <c>PostAsJsonAsync</c> and minimal-API binding.
/// </summary>
public sealed class AdminCliContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CreateUserBodyBindsToCreateUserRequest()
    {
        var json = JsonSerializer.Serialize(new CreateUserBody("alice", "Alice", "human", true), Web);

        var req = JsonSerializer.Deserialize<CreateUserRequest>(json, Web);

        req.Should().NotBeNull();
        req!.GithubUsername.Should().Be("alice");
        req.DisplayName.Should().Be("Alice");
        req.ActorType.Should().Be("human");
        req.IsAdmin.Should().BeTrue();
    }

    [Fact]
    public void GrantBodyBindsToGrantRequest()
    {
        var json = JsonSerializer.Serialize(new GrantBody("can_override_verdict", true), Web);

        var req = JsonSerializer.Deserialize<GrantRequest>(json, Web);

        req.Should().NotBeNull();
        req!.Permission.Should().Be("can_override_verdict");
        req.Value.Should().BeTrue();
    }

    [Fact]
    public void SnakeCaseBodyDoesNotBind()
    {
        var req = JsonSerializer.Deserialize<CreateUserRequest>("""{"github_username":"alice"}""", Web);

        req!.GithubUsername.Should().BeNull();
    }
}
