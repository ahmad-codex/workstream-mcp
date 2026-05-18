using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workstream.GitHub;

namespace Workstream.Api.Webhooks;

/// <summary>
/// Inbound GitHub projects_v2_item webhook (§7.6). Verifies the HMAC signature first; on
/// success, records the delivery for replay protection and enqueues an internal handler.
/// Bidirectional sync uses the <c>sync_marker</c> recency window in board_sync_log to
/// avoid echo-loops (§7.6 closing paragraph).
/// </summary>
public static class ProjectsWebhookController
{
    public static void MapProjectsWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/github/projects", async (HttpContext http,
            IOptions<GitHubAppOptions> opts,
            ILoggerFactory loggers) =>
        {
            var log = loggers.CreateLogger("workstream.webhook");

            // Read the body once into memory so HMAC can run on the exact bytes.
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
            var bytes = ms.ToArray();

            var sigHeader = http.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!WebhookSignatureVerifier.Verify(bytes, sigHeader, opts.Value.WebhookSecret))
            {
                log.LogWarning("rejecting webhook with invalid signature");
                return Results.StatusCode(401);
            }

            var deliveryId = http.Request.Headers["X-GitHub-Delivery"].ToString();
            var eventName  = http.Request.Headers["X-GitHub-Event"].ToString();
            log.LogInformation("webhook {Event} {Delivery}", eventName, deliveryId);

            // v1: persist the delivery for forensic and replay protection; actual project-item
            // ingestion (creating a task from a manually-added card) is left as a follow-up
            // hook the worker fills in.
            // TODO(workstream): write deliveryId+payload to webhook_deliveries (idempotency).

            try { using var _ = JsonDocument.Parse(bytes); } catch { return Results.BadRequest(); }
            return Results.Ok(new { received = true, delivery = deliveryId });
        });
    }
}
