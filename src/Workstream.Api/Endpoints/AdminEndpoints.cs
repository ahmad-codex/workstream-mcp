using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Workstream.Api.Middleware;
using Workstream.Core.Domain;
using Workstream.Data;
using Workstream.Data.Repositories;
using Workstream.GitHub;

namespace Workstream.Api.Endpoints;

/// <summary>
/// Admin HTTP endpoints (§10.1). Gated by the static <c>WORKSTREAM_ADMIN_TOKEN</c> bearer.
/// The token-resolution middleware does the auth check before routing here; if this method
/// runs, the caller has already proven they hold the admin bearer.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/admin");

        grp.MapPost("/users", async (CreateUserRequest req, IUserRepository users, HttpContext http, IConfiguration cfg) =>
        {
            var token = GenerateUrlToken();
            var u = await users.CreateAsync(
                githubUsername: req.GithubUsername,
                displayName: req.DisplayName,
                actorType: req.ActorType ?? ActorType.Human,
                token: token,
                isAdmin: req.IsAdmin ?? false,
                ct: http.RequestAborted).ConfigureAwait(false);

            var baseUrl = cfg["WORKSTREAM_PUBLIC_BASE_URL"]
                          ?? Environment.GetEnvironmentVariable("WORKSTREAM_PUBLIC_BASE_URL")
                          ?? $"{http.Request.Scheme}://{http.Request.Host}";
            return Results.Json(new
            {
                user = new { id = u.Id, github_username = u.GithubUsername, display_name = u.DisplayName, actor_type = u.ActorType, is_admin = u.IsAdmin },
                url = $"{baseUrl.TrimEnd('/')}/{token}",
                mcp_endpoint = $"{baseUrl.TrimEnd('/')}/{token}/mcp",
                warning = "This URL contains the user's bearer token. Copy it once — it will not be shown again.",
            });
        });

        grp.MapPost("/users/{username}/rotate-token", async (string username, IUserRepository users, HttpContext http) =>
        {
            var existing = await users.GetByUsernameAsync(username, http.RequestAborted).ConfigureAwait(false);
            if (existing is null) return Results.NotFound(new { error = "user not found" });

            // We don't store the live token in the events table; rotation hashes the OLD value
            // (which the server can't recover at this point — the old token lives only with the
            // user). Since we don't have the old token here, we record a placeholder hash.
            var newToken = GenerateUrlToken();
            var placeholderHash = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray()));
            var rotated = await users.RotateTokenAsync(existing.Id, newToken, placeholderHash, TimeSpan.FromDays(30), http.RequestAborted)
                .ConfigureAwait(false);
            if (rotated is null) return Results.NotFound(new { error = "user not found" });

            return Results.Json(new
            {
                user = new { id = rotated.Id, github_username = rotated.GithubUsername },
                url = $"{http.Request.Scheme}://{http.Request.Host}/{newToken}",
                warning = "Copy this URL once — it will not be shown again.",
            });
        });

        grp.MapPost("/users/{username}/permissions", async (string username, GrantRequest req, IUserRepository users, HttpContext http) =>
        {
            var existing = await users.GetByUsernameAsync(username, http.RequestAborted).ConfigureAwait(false);
            if (existing is null) return Results.NotFound(new { error = "user not found" });
            await users.SetPermissionAsync(existing.Id, req.Permission, req.Value, http.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { ok = true, github_username = username, permission = req.Permission, value = req.Value });
        });

        // Diagnostic helper: list every V2 project the installed App can see on an org.
        // If discover returns NOT_FOUND, this confirms what numbers are actually visible.
        grp.MapPost("/boards/list", async (DiscoverBoardRequest req, ProjectsV2Client gh, HttpContext http) =>
        {
            try
            {
                var rows = await gh.ListOrgProjectsAsync(req.Org, http.RequestAborted).ConfigureAwait(false);
                return Results.Json(new { projects = rows });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        // GitHub Projects V2 discovery — one-shot, given org login + project number, queries
        // GraphQL with the installed App's token and returns every node id needed to register
        // the board (project id, status field id, status option ids). Saves the operator from
        // having to construct a GraphQL query by hand.
        grp.MapPost("/boards/discover", async (DiscoverBoardRequest req, ProjectsV2Client gh, HttpContext http) =>
        {
            try
            {
                var result = await gh.DiscoverOrgBoardAsync(req.Org, req.ProjectNumber, http.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Json(new
                {
                    project_node_id     = result.ProjectNodeId,
                    project_number      = result.ProjectNumber,
                    title               = result.Title,
                    status_field_node_id = result.StatusFieldNodeId,
                    status_options      = result.StatusOptions,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Bind an already-registered board to a plan. Just sets plans.primary_board_id;
        // exists because there's no MCP tool for this yet and updating the column manually
        // is friction.
        grp.MapPost("/plans/{planId:guid}/board", async (Guid planId, BindBoardRequest req, IDbConnectionFactory factory, HttpContext http) =>
        {
            await using var conn = await factory.OpenAsync(http.RequestAborted).ConfigureAwait(false);
            var n = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE plans SET primary_board_id = @boardId WHERE id = @planId",
                new { planId, boardId = req.BoardId }, cancellationToken: http.RequestAborted))
                .ConfigureAwait(false);
            return n == 0
                ? Results.NotFound(new { error = "plan not found" })
                : Results.Json(new { ok = true, plan_id = planId, board_id = req.BoardId });
        });
    }

    /// <summary>32 bytes (256 bits) of CSPRNG, URL-safe base64, no padding (§3.1).</summary>
    private static string GenerateUrlToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed record CreateUserRequest(string GithubUsername, string? DisplayName, string? ActorType, bool? IsAdmin);
public sealed record GrantRequest(string Permission, bool Value);
public sealed record DiscoverBoardRequest(string Org, int ProjectNumber);
public sealed record BindBoardRequest(Guid BoardId);
