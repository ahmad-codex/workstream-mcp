using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Workstream.Core.Domain;

namespace Workstream.Api.Middleware;

/// <summary>
/// Per-actor sliding-window rate limit (§13.3). Defaults: 60 calls / minute, 600 / hour,
/// burst of 30. Returns HTTP 429 when exceeded; the response body includes a structured
/// service_unavailable code so MCP callers see it as a normal tool error rather than a
/// transport panic.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ConcurrentDictionary<Guid, ActorBucket> _buckets = new();

    public RateLimitMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(TokenResolutionMiddleware.ContextKey, out var rc) && rc is RequestContext requestCtx)
        {
            var bucket = _buckets.GetOrAdd(requestCtx.ActorId, _ => new ActorBucket());
            if (!bucket.TryAcquire(DateTimeOffset.UtcNow))
            {
                ctx.Response.StatusCode = 429;
                await ctx.Response.WriteAsync("""{"ok":false,"error":{"code":"service_unavailable","message":"rate limit exceeded"}}""").ConfigureAwait(false);
                return;
            }
        }
        await _next(ctx).ConfigureAwait(false);
    }

    private sealed class ActorBucket
    {
        private const int PerMinute = 60;
        private const int PerHour   = 600;
        private readonly object _lock = new();
        private readonly System.Collections.Generic.Queue<DateTimeOffset> _minuteWindow = new();
        private readonly System.Collections.Generic.Queue<DateTimeOffset> _hourWindow   = new();

        public bool TryAcquire(DateTimeOffset now)
        {
            lock (_lock)
            {
                while (_minuteWindow.Count > 0 && now - _minuteWindow.Peek() > TimeSpan.FromMinutes(1))
                    _minuteWindow.Dequeue();
                while (_hourWindow.Count > 0 && now - _hourWindow.Peek() > TimeSpan.FromHours(1))
                    _hourWindow.Dequeue();
                if (_minuteWindow.Count >= PerMinute) return false;
                if (_hourWindow.Count   >= PerHour)   return false;
                _minuteWindow.Enqueue(now);
                _hourWindow.Enqueue(now);
                return true;
            }
        }
    }
}
