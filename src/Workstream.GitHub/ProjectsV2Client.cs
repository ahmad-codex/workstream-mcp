using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Workstream.GitHub;

/// <summary>
/// Thin Projects V2 GraphQL client (§7.1). The four operations we use are inlined as raw
/// GraphQL strings rather than via Octokit.GraphQL.NET's typed builder, because V2 schema
/// coverage in the typed library lags behind GitHub. Raw is more honest.
/// </summary>
public sealed class ProjectsV2Client
{
    private readonly HttpClient _http;
    private readonly GitHubAppAuthService _auth;
    private readonly ILogger<ProjectsV2Client>? _log;

    public ProjectsV2Client(HttpClient http, GitHubAppAuthService auth, ILogger<ProjectsV2Client>? log = null)
    {
        _http = http;
        _auth = auth;
        _log = log;
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri("https://api.github.com/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("workstream-mcp/0.1");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
    }

    public async Task<DraftItemResult> CreateDraftItemAsync(string projectNodeId, string title, string body, CancellationToken ct = default)
    {
        const string query = """
            mutation($projectId: ID!, $title: String!, $body: String!) {
              addProjectV2DraftIssue(input: { projectId: $projectId, title: $title, body: $body }) {
                projectItem { id databaseId }
              }
            }
            """;
        var resp = await SendAsync(query, new { projectId = projectNodeId, title, body }, ct).ConfigureAwait(false);
        var item = resp.RootElement.GetProperty("data").GetProperty("addProjectV2DraftIssue").GetProperty("projectItem");
        return new DraftItemResult(
            NodeId:     item.GetProperty("id").GetString()!,
            DatabaseId: item.GetProperty("databaseId").GetInt64());
    }

    /// <summary>
    /// Lookup the numeric databaseId for an existing project-item node id. Used to
    /// backfill tasks created before the database-id column existed.
    /// </summary>
    public async Task<long?> LookupItemDatabaseIdAsync(string itemNodeId, CancellationToken ct = default)
    {
        const string query = """
            query($id: ID!) {
              node(id: $id) {
                ... on ProjectV2Item { id databaseId }
              }
            }
            """;
        var resp = await SendAsync(query, new { id = itemNodeId }, ct).ConfigureAwait(false);
        var node = resp.RootElement.GetProperty("data").GetProperty("node");
        if (node.ValueKind == JsonValueKind.Null) return null;
        if (!node.TryGetProperty("databaseId", out var db) || db.ValueKind != JsonValueKind.Number) return null;
        return db.GetInt64();
    }

    /// <summary>
    /// Resolve the inner DraftIssue node id for a ProjectV2Item (the assignment mutation
    /// takes that, not the wrapping item). Returns null if the item isn't a draft.
    /// </summary>
    public async Task<string?> LookupDraftIssueIdAsync(string itemNodeId, CancellationToken ct = default)
    {
        const string query = """
            query($itemId: ID!) {
              node(id: $itemId) {
                ... on ProjectV2Item { content { ... on DraftIssue { id } } }
              }
            }
            """;
        var resp = await SendAsync(query, new { itemId = itemNodeId }, ct).ConfigureAwait(false);
        var node = resp.RootElement.GetProperty("data").GetProperty("node");
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) return null;
        if (!content.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
        return id.GetString();
    }

    /// <summary>
    /// One round-trip to resolve the data needed to assign a Projects V2 draft item to a
    /// GitHub user: the inner DraftIssue node id (the assignment mutation takes that, not
    /// the wrapping ProjectV2Item) and the user's node id. Returns nulls when either
    /// lookup fails (user doesn't exist on GitHub, item isn't a draft, etc.) so the
    /// caller can skip without failing the whole board sync.
    /// </summary>
    public async Task<(string? DraftIssueNodeId, string? UserNodeId)> LookupDraftAndUserAsync(
        string itemNodeId, string githubLogin, CancellationToken ct = default)
    {
        const string query = """
            query($itemId: ID!, $login: String!) {
              item: node(id: $itemId) {
                ... on ProjectV2Item {
                  content { ... on DraftIssue { id } }
                }
              }
              user(login: $login) { id }
            }
            """;
        var resp = await SendAsync(query, new { itemId = itemNodeId, login = githubLogin }, ct).ConfigureAwait(false);
        var data = resp.RootElement.GetProperty("data");
        string? draftId = null;
        if (data.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("id", out var did) && did.ValueKind == JsonValueKind.String)
        {
            draftId = did.GetString();
        }
        string? userId = null;
        if (data.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("id", out var uid) && uid.ValueKind == JsonValueKind.String)
        {
            userId = uid.GetString();
        }
        return (draftId, userId);
    }

    /// <summary>
    /// Replace the assignee list on a Projects V2 draft issue. Empty list clears all
    /// assignees. Note: <paramref name="draftIssueNodeId"/> is the DraftIssue id, not
    /// the wrapping ProjectV2Item id — resolve via <see cref="LookupDraftAndUserAsync"/>.
    /// </summary>
    public async Task UpdateDraftIssueAssigneesAsync(string draftIssueNodeId, IReadOnlyList<string> assigneeNodeIds, CancellationToken ct = default)
    {
        const string query = """
            mutation($draftIssueId: ID!, $assigneeIds: [ID!]) {
              updateProjectV2DraftIssue(input: { draftIssueId: $draftIssueId, assigneeIds: $assigneeIds }) {
                draftIssue { id }
              }
            }
            """;
        await SendAsync(query, new { draftIssueId = draftIssueNodeId, assigneeIds = assigneeNodeIds }, ct).ConfigureAwait(false);
    }

    public async Task UpdateItemStatusFieldAsync(string projectNodeId, string itemNodeId, string statusFieldNodeId, string optionId, CancellationToken ct = default)
    {
        const string query = """
            mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
              updateProjectV2ItemFieldValue(input: {
                projectId: $projectId, itemId: $itemId, fieldId: $fieldId,
                value: { singleSelectOptionId: $optionId }
              }) {
                projectV2Item { id }
              }
            }
            """;
        await SendAsync(query, new
        {
            projectId = projectNodeId, itemId = itemNodeId, fieldId = statusFieldNodeId, optionId,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// List all V2 projects on an org/user that the installed App can see. Useful when
    /// discover fails — confirms the App has projects:read and shows actual numbers.
    /// </summary>
    public async Task<IReadOnlyList<(int Number, string Title, string Id)>> ListOrgProjectsAsync(string org, CancellationToken ct = default)
    {
        const string query = """
            query($org: String!) {
              organization(login: $org) {
                projectsV2(first: 20) { nodes { number title id } }
              }
            }
            """;
        var resp = await SendAsync(query, new { org }, ct).ConfigureAwait(false);
        var list = new List<(int, string, string)>();
        var nodes = resp.RootElement.GetProperty("data").GetProperty("organization").GetProperty("projectsV2").GetProperty("nodes");
        foreach (var n in nodes.EnumerateArray())
        {
            list.Add((n.GetProperty("number").GetInt32(), n.GetProperty("title").GetString()!, n.GetProperty("id").GetString()!));
        }
        return list;
    }

    /// <summary>
    /// One-shot discovery: given org login + project number, return the project node id,
    /// the Status field node id, and every Status option's (name → id) mapping. Used by
    /// <c>/admin/boards/discover</c> to populate <c>project_boards</c> for a new board.
    /// </summary>
    public async Task<BoardDiscoveryResult> DiscoverOrgBoardAsync(string org, int projectNumber, CancellationToken ct = default)
    {
        const string query = """
            query($org: String!, $number: Int!) {
              organization(login: $org) {
                projectV2(number: $number) {
                  id
                  title
                  field(name: "Status") {
                    ... on ProjectV2SingleSelectField {
                      id
                      options { id name }
                    }
                  }
                }
              }
            }
            """;
        var resp = await SendAsync(query, new { org, number = projectNumber }, ct).ConfigureAwait(false);
        var project = resp.RootElement.GetProperty("data").GetProperty("organization").GetProperty("projectV2");
        var projectNodeId    = project.GetProperty("id").GetString()!;
        var title            = project.GetProperty("title").GetString()!;
        var field            = project.GetProperty("field");
        var fieldNodeId      = field.GetProperty("id").GetString()!;
        var options          = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in field.GetProperty("options").EnumerateArray())
            options[opt.GetProperty("name").GetString()!] = opt.GetProperty("id").GetString()!;
        return new BoardDiscoveryResult(projectNodeId, projectNumber, title, fieldNodeId, options);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetStatusOptionsAsync(string projectNodeId, string fieldName = "Status", CancellationToken ct = default)
    {
        const string query = """
            query($projectId: ID!) {
              node(id: $projectId) {
                ... on ProjectV2 {
                  field(name: "Status") {
                    ... on ProjectV2SingleSelectField {
                      id
                      options { id name }
                    }
                  }
                }
              }
            }
            """;
        var resp = await SendAsync(query, new { projectId = projectNodeId }, ct).ConfigureAwait(false);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = resp.RootElement.GetProperty("data").GetProperty("node").GetProperty("field").GetProperty("options");
        foreach (var opt in options.EnumerateArray())
        {
            dict[opt.GetProperty("name").GetString()!] = opt.GetProperty("id").GetString()!;
        }
        return dict;
    }

    /// <summary>
    /// Replace the body (description) of a Projects V2 draft issue. Used to keep a live
    /// "audit ledger" on the task's card — the finding-by-finding lifecycle that never
    /// moves the card between columns. <paramref name="draftIssueNodeId"/> is the inner
    /// DraftIssue id — resolve via <see cref="LookupDraftIssueIdAsync"/>.
    /// </summary>
    public async Task UpdateDraftIssueBodyAsync(string draftIssueNodeId, string body, CancellationToken ct = default)
    {
        const string query = """
            mutation($draftIssueId: ID!, $body: String!) {
              updateProjectV2DraftIssue(input: { draftIssueId: $draftIssueId, body: $body }) {
                draftIssue { id }
              }
            }
            """;
        await SendAsync(query, new { draftIssueId = draftIssueNodeId, body }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Look up a single-select field on a project by name (e.g. "Audit Stage"). Returns
    /// the field node id and its option name→id map, or null when the project has no
    /// such field — callers treat a missing field as "feature not configured" and skip
    /// it, so the board can opt in just by adding the field in GitHub.
    /// </summary>
    public async Task<SingleSelectFieldInfo?> GetSingleSelectFieldAsync(string projectNodeId, string fieldName, CancellationToken ct = default)
    {
        const string query = """
            query($projectId: ID!, $fieldName: String!) {
              node(id: $projectId) {
                ... on ProjectV2 {
                  field(name: $fieldName) {
                    ... on ProjectV2SingleSelectField { id name options { id name } }
                  }
                }
              }
            }
            """;
        var resp = await SendAsync(query, new { projectId = projectNodeId, fieldName }, ct).ConfigureAwait(false);
        var node = resp.RootElement.GetProperty("data").GetProperty("node");
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (!node.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.Object) return null;
        if (!field.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (field.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in opts.EnumerateArray())
                options[opt.GetProperty("name").GetString()!] = opt.GetProperty("id").GetString()!;
        }
        return new SingleSelectFieldInfo(idEl.GetString()!, options);
    }

    private async Task<JsonDocument> SendAsync(string query, object variables, CancellationToken ct)
    {
        var token = await _auth.GetInstallationTokenAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Post, "graphql");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(new { query, variables });
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _log?.LogWarning("GitHub GraphQL non-2xx: {Status} {Body}", resp.StatusCode, content);
            throw new HttpRequestException($"GitHub returned {(int)resp.StatusCode}: {content}");
        }
        var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0)
        {
            throw new HttpRequestException($"GitHub GraphQL errors: {errs}");
        }
        return doc;
    }
}

public sealed record BoardDiscoveryResult(
    string ProjectNodeId,
    int    ProjectNumber,
    string Title,
    string StatusFieldNodeId,
    IReadOnlyDictionary<string, string> StatusOptions);

public sealed record DraftItemResult(string NodeId, long DatabaseId);

/// <summary>A Projects V2 single-select field: its node id and option name→id map.</summary>
public sealed record SingleSelectFieldInfo(
    string FieldId,
    IReadOnlyDictionary<string, string> Options);
