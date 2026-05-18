using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Workstream.Core.Domain;
using Workstream.Data.Repositories;

namespace Workstream.Api.Middleware;

/// <summary>
/// Resolves <c>/{token}/...</c> path-segment tokens to a <see cref="User"/> row and attaches
/// a <see cref="RequestContext"/> to <see cref="HttpContext.Items"/> under the key
/// <c>workstream.request_context</c> (§3.2). On any failure returns a bare 404 — the spec
/// requires 404 not 401, so the token namespace is not enumerable.
///
/// Admin endpoints (paths starting with /admin) take a separate code path: they require the
/// <c>WORKSTREAM_ADMIN_TOKEN</c> bearer (§3.4) and synthesize a system-admin RequestContext.
/// </summary>
public sealed class TokenResolutionMiddleware
{
    public const string ContextKey = "workstream.request_context";

    private readonly RequestDelegate _next;

    public TokenResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, IUserRepository users, IOptions opts)
    {
        var path = ctx.Request.Path.Value ?? "/";

        // Webhook and health pass-through (no token required).
        if (path.StartsWith("/webhooks/", StringComparison.Ordinal) ||
            path == "/health" || path == "/healthz" || path == "/")
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        // Admin endpoints: separate bearer.
        if (path.StartsWith("/admin", StringComparison.Ordinal))
        {
            if (!TryAdminAuth(ctx, opts.AdminToken, out var adminCtx))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            ctx.Items[ContextKey] = adminCtx;
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        // /<token>/... — extract first segment.
        var segments = path.TrimStart('/').Split('/', 2);
        if (segments.Length == 0 || string.IsNullOrEmpty(segments[0]))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var token = segments[0];
        var user = await users.ResolveByTokenAsync(token, ctx.RequestAborted).ConfigureAwait(false);
        if (user is null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        // Also reject if the token's hash appears in revoked_tokens.
        var hash = HashToken(token);
        if (await users.IsTokenRevokedAsync(hash, ctx.RequestAborted).ConfigureAwait(false))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var requestCtx = new RequestContext(
            ActorId: user.Id,
            ActorType: user.ActorType,
            GithubUsername: user.GithubUsername,
            IsAdmin: user.IsAdmin,
            CanOverrideVerdict: user.CanOverrideVerdict,
            CanArchivePlan: user.CanArchivePlan,
            CanMarkNeedsHumanReview: user.CanMarkNeedsHumanReview,
            DisplayName: user.DisplayName,
            TraceId: System.Diagnostics.Activity.Current?.Id ?? Guid.NewGuid().ToString("N"),
            Now: DateTimeOffset.UtcNow);

        ctx.Items[ContextKey] = requestCtx;

        // Rewrite the path so downstream routes don't see the token.
        ctx.Request.Path = "/" + (segments.Length > 1 ? segments[1] : "");

        await _next(ctx).ConfigureAwait(false);
    }

    private static bool TryAdminAuth(HttpContext ctx, string expected, out RequestContext adminCtx)
    {
        adminCtx = null!;
        if (string.IsNullOrEmpty(expected)) return false;
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var auth)) return false;
        var v = auth.ToString();
        if (!v.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var token = v["Bearer ".Length..];
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected))) return false;
        adminCtx = new RequestContext(
            ActorId: Guid.Empty,
            ActorType: "system",
            GithubUsername: "workstream-admin",
            IsAdmin: true,
            CanOverrideVerdict: true,
            CanArchivePlan: true,
            CanMarkNeedsHumanReview: true,
            TraceId: Guid.NewGuid().ToString("N"),
            Now: DateTimeOffset.UtcNow);
        return true;
    }

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public sealed class Options : IOptions
    {
        public string AdminToken { get; init; } = "";
    }

    public interface IOptions { string AdminToken { get; } }
}
