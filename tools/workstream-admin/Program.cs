using System;
using System.CommandLine;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Workstream.Admin;

/// <summary>
/// workstream-admin — operator CLI for the Workstream MCP server (§10.1).
///
/// The CLI talks to the server's admin endpoints over HTTPS with the static
/// <c>WORKSTREAM_ADMIN_TOKEN</c> bearer. Configuration:
///   * <c>WORKSTREAM_ADMIN_URL</c>   — e.g. https://mcp.example.com
///   * <c>WORKSTREAM_ADMIN_TOKEN</c> — the bearer token
/// Optionally a <c>.workstream-admin.toml</c> in the working dir overrides these.
///
/// Every command prints the server's response.
///
/// Projects, repos, boards, Slack settings and plans are managed through the admin-gated MCP
/// setup tools (<c>create_project</c>, <c>add_project_repo</c>, <c>create_plan</c>,
/// <c>activate_plan</c>, ...) called by a user created with <c>is_admin</c>; the server has
/// no REST endpoints for them.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = BuildRootCommand();
        return await root.InvokeAsync(args).ConfigureAwait(false);
    }

    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("workstream-admin — operator CLI for the Workstream MCP server");
        root.Add(BuildUserCommand());
        root.Add(BuildBootstrapCommand());
        return root;
    }

    private static Command BuildUserCommand()
    {
        var user = new Command("user", "User management");

        var create = new Command("create", "Create a user and print their MCP URL once");
        var usernameOpt = new Option<string>("--github-username") { IsRequired = true };
        var displayOpt  = new Option<string?>("--display");
        var typeOpt     = new Option<string>("--type", () => "human");
        var adminOpt    = new Option<bool>("--admin", () => false, "Allow the user to call the admin-gated setup tools");
        create.AddOption(usernameOpt);
        create.AddOption(displayOpt);
        create.AddOption(typeOpt);
        create.AddOption(adminOpt);
        create.SetHandler(async (string username, string? display, string type, bool isAdmin) =>
        {
            var client = AdminClient.FromEnv();
            var resp = await client.PostAsync("/admin/users", new CreateUserBody(username, display, type, isAdmin))
                .ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(resp, new JsonSerializerOptions { WriteIndented = true }));
        }, usernameOpt, displayOpt, typeOpt, adminOpt);
        user.Add(create);

        var rotate = new Command("rotate-token", "Rotate a user's MCP URL token");
        var rotUserOpt = new Option<string>("--github-username") { IsRequired = true };
        rotate.AddOption(rotUserOpt);
        rotate.SetHandler(async (string username) =>
        {
            var client = AdminClient.FromEnv();
            var resp = await client.PostAsync($"/admin/users/{Uri.EscapeDataString(username)}/rotate-token", new { }).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(resp, new JsonSerializerOptions { WriteIndented = true }));
        }, rotUserOpt);
        user.Add(rotate);

        var grant = new Command("grant", "Grant a permission flag");
        var grantUser = new Option<string>("--github-username") { IsRequired = true };
        var permOpt   = new Option<string>("--permission") { IsRequired = true };
        grant.AddOption(grantUser);
        grant.AddOption(permOpt);
        grant.SetHandler(async (string username, string perm) =>
        {
            var client = AdminClient.FromEnv();
            await client.PostAsync($"/admin/users/{Uri.EscapeDataString(username)}/permissions",
                new GrantBody(perm, true)).ConfigureAwait(false);
            Console.WriteLine($"granted {perm} to {username}");
        }, grantUser, permOpt);
        user.Add(grant);

        return user;
    }

    private static Command BuildBootstrapCommand()
    {
        var apply = new Command("apply", "Apply a bootstrap.yml describing the desired state");
        var fileArg = new Argument<string>("file");
        apply.AddArgument(fileArg);
        apply.SetHandler((string file) =>
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"file not found: {file}");
                Environment.Exit(2);
            }
            var content = File.ReadAllText(file);
            Console.WriteLine($"# applying {file}");
            // Bootstrap apply is deferred to v1.1: parse YAML, diff against /admin/state, emit
            // create/update/delete calls. For now we just echo the file so operators see the
            // CLI is wired and they can iterate on the YAML schema offline.
            Console.WriteLine(content);
        }, fileArg);
        return apply;
    }
}

/// <summary>Body of <c>POST /admin/users</c>. Serialized with web defaults (camelCase) to match the server's <c>CreateUserRequest</c>.</summary>
public sealed record CreateUserBody(string GithubUsername, string? DisplayName, string ActorType, bool IsAdmin);

/// <summary>Body of <c>POST /admin/users/{username}/permissions</c>.</summary>
public sealed record GrantBody(string Permission, bool Value);

internal sealed class AdminClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;

    public AdminClient(string baseUrl, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static AdminClient FromEnv()
    {
        var url = Environment.GetEnvironmentVariable("WORKSTREAM_ADMIN_URL")
                  ?? throw new InvalidOperationException("WORKSTREAM_ADMIN_URL not set");
        var tok = Environment.GetEnvironmentVariable("WORKSTREAM_ADMIN_TOKEN")
                  ?? throw new InvalidOperationException("WORKSTREAM_ADMIN_TOKEN not set");
        return new AdminClient(url, tok);
    }

    public async Task<object?> PostAsync(string path, object body)
    {
        using var resp = await _http.PostAsJsonAsync(_baseUrl + path, body).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<object>().ConfigureAwait(false);
    }

    public async Task<object?> GetAsync(string path)
    {
        using var resp = await _http.GetAsync(_baseUrl + path).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<object>().ConfigureAwait(false);
    }
}
