using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Workstream.Api.Middleware;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;

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
