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
/// Every command prints the request and response for auditability.
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
        root.Add(BuildProjectCommand());
        root.Add(BuildPlanCommand());
        root.Add(BuildPlanTypeCommand());
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
        create.AddOption(usernameOpt);
        create.AddOption(displayOpt);
        create.AddOption(typeOpt);
        create.SetHandler(async (string username, string? display, string type) =>
        {
            var client = AdminClient.FromEnv();
            var resp = await client.PostAsync("/admin/users", new { github_username = username, display_name = display, actor_type = type })
                .ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(resp, new JsonSerializerOptions { WriteIndented = true }));
        }, usernameOpt, displayOpt, typeOpt);
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
                new { permission = perm, value = true }).ConfigureAwait(false);
            Console.WriteLine($"granted {perm} to {username}");
        }, grantUser, permOpt);
        user.Add(grant);

        return user;
    }

    private static Command BuildProjectCommand()
    {
        var project = new Command("project", "Project management");

        var create = new Command("create");
        var slug = new Option<string>("--slug") { IsRequired = true };
        var name = new Option<string>("--name") { IsRequired = true };
        create.AddOption(slug); create.AddOption(name);
        create.SetHandler(async (string s, string n) =>
        {
            var c = AdminClient.FromEnv();
            var r = await c.PostAsync("/admin/projects", new { slug = s, display_name = n }).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(r));
        }, slug, name);
        project.Add(create);

        var addRepo = new Command("add-repo");
        var p1 = new Option<string>("--slug") { IsRequired = true };
        var owner = new Option<string>("--owner") { IsRequired = true };
        var repo = new Option<string>("--repo") { IsRequired = true };
        var refOnly = new Option<bool>("--reference-only", () => false);
        addRepo.AddOption(p1); addRepo.AddOption(owner); addRepo.AddOption(repo); addRepo.AddOption(refOnly);
        addRepo.SetHandler(async (string s, string o, string r, bool refonly) =>
        {
            var c = AdminClient.FromEnv();
            var x = await c.PostAsync($"/admin/projects/{Uri.EscapeDataString(s)}/repos",
                new { github_owner = o, github_repo = r, is_reference_only = refonly }).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(x));
        }, p1, owner, repo, refOnly);
        project.Add(addRepo);

        return project;
    }

    private static Command BuildPlanCommand()
    {
        var plan = new Command("plan", "Plan management");
        var create = new Command("create");
        var projectOpt = new Option<string>("--project") { IsRequired = true };
        var typeOpt    = new Option<string>("--type") { IsRequired = true };
        var nameOpt    = new Option<string>("--name") { IsRequired = true };
        create.AddOption(projectOpt); create.AddOption(typeOpt); create.AddOption(nameOpt);
        create.SetHandler(async (string p, string t, string n) =>
        {
            var c = AdminClient.FromEnv();
            var r = await c.PostAsync("/admin/plans", new { project_slug = p, plan_type = t, name = n }).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(r));
        }, projectOpt, typeOpt, nameOpt);
        plan.Add(create);

        var activate = new Command("activate");
        var idOpt = new Option<string>("--id") { IsRequired = true };
        activate.AddOption(idOpt);
        activate.SetHandler(async (string id) =>
        {
            var c = AdminClient.FromEnv();
            var r = await c.PostAsync($"/admin/plans/{id}/activate", new { }).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(r));
        }, idOpt);
        plan.Add(activate);

        return plan;
    }

    private static Command BuildPlanTypeCommand()
    {
        var pt = new Command("plan-type", "Plan-type definition management");
        var list = new Command("list");
        list.SetHandler(async () =>
        {
            var c = AdminClient.FromEnv();
            var r = await c.GetAsync("/admin/plan-types").ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(r));
        });
        pt.Add(list);
        return pt;
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
