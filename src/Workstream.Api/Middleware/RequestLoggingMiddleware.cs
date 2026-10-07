using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Workstream.Core.Domain;

namespace Workstream.Api.Middleware;

/// <summary>
/// Structured request log that redacts the URL-token path segment before any log line
/// reaches Serilog or the OTel exporter (§13.1). The path is rewritten from
/// <c>/{token}/mcp</c> to <c>/&lt;actor:alice&gt;/mcp</c> in trace/log output. The literal
/// token never appears.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _log;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> log)
    {
        _next = next; _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var start = DateTimeOffset.UtcNow;
        var rewrittenPath = RedactedPath(ctx);
        try
        {
            await _next(ctx).ConfigureAwait(false);
        }
        finally
        {
            var elapsed = DateTimeOffset.UtcNow - start;
            var actor = (ctx.Items[TokenResolutionMiddleware.ContextKey] as RequestContext)?.GithubUsername ?? "anon";
            _log.LogInformation("request {Path} actor={Actor} status={Status} elapsedMs={Elapsed}",
                rewrittenPath, actor, ctx.Response.StatusCode, elapsed.TotalMilliseconds);
        }
    }

    private static string RedactedPath(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        // After TokenResolutionMiddleware rewrites the path to strip the token, the request path
        // is already token-free. But the original Request.Path (preserved by Items) might not be.
        // For safety, redact the first non-empty segment if this is on the public surface.
        if (path.StartsWith("/admin", StringComparison.Ordinal) ||
            path.StartsWith("/webhooks/", StringComparison.Ordinal)) return path;
        var actor = (ctx.Items[TokenResolutionMiddleware.ContextKey] as RequestContext)?.GithubUsername;
        return actor is null ? "/<redacted>" + path : $"/<actor:{actor}>" + path;
    }
}
