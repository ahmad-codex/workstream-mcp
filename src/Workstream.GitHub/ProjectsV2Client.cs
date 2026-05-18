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

    public async Task<string> CreateDraftItemAsync(string projectNodeId, string title, string body, CancellationToken ct = default)
    {
        const string query = """
            mutation($projectId: ID!, $title: String!, $body: String!) {
              addProjectV2DraftIssue(input: { projectId: $projectId, title: $title, body: $body }) {
                projectItem { id }
              }
            }
            """;
        var resp = await SendAsync(query, new { projectId = projectNodeId, title, body }, ct).ConfigureAwait(false);
        return resp.RootElement.GetProperty("data").GetProperty("addProjectV2DraftIssue")
            .GetProperty("projectItem").GetProperty("id").GetString()!;
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
