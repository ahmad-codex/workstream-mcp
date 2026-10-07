using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Workstream.Telemetry;

/// <summary>
/// Wires OTel resource, propagators, and exporters (§11.2). The exporter target is
/// configurable via <c>WORKSTREAM_OTEL_ENDPOINT</c>; the API host only calls this when that
/// variable is set. The URL token in the request path is redacted from server spans.
/// </summary>
public static class TelemetryBootstrap
{
    public static IServiceCollection AddWorkstreamTelemetry(this IServiceCollection services, string? otelEndpoint)
    {
        services.AddSingleton<WorkstreamMetrics>();

        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName: "workstream-mcp", serviceVersion: "0.1.0");

        services.AddOpenTelemetry()
            .WithMetrics(b =>
            {
                b.SetResourceBuilder(resource);
                b.AddMeter(WorkstreamMetrics.MeterName);
                b.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel");
                b.AddAspNetCoreInstrumentation();
                b.AddHttpClientInstrumentation();
                b.AddRuntimeInstrumentation();
                if (!string.IsNullOrEmpty(otelEndpoint))
                    b.AddOtlpExporter(o => o.Endpoint = new System.Uri(otelEndpoint));
            })
            .WithTracing(b =>
            {
                b.SetResourceBuilder(resource);
                b.AddSource("Npgsql");
                b.AddAspNetCoreInstrumentation(o => o.EnrichWithHttpRequest = (activity, request) =>
                    activity.SetTag("url.path", RedactTokenPath(request.Path.Value)));
                b.AddHttpClientInstrumentation();
                if (!string.IsNullOrEmpty(otelEndpoint))
                    b.AddOtlpExporter(o => o.Endpoint = new System.Uri(otelEndpoint));
            });

        return services;
    }

    private static readonly HashSet<string> KnownFirstSegments =
        new(StringComparer.OrdinalIgnoreCase) { "admin", "webhooks", "healthz", "mcp" };

    /// <summary>
    /// Replaces the URL token (the first path segment of <c>/{token}/...</c>) with
    /// <c>&lt;token&gt;</c> so it never reaches a trace exporter. Paths that start with a
    /// known route segment are returned unchanged.
    /// </summary>
    public static string RedactTokenPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return path ?? "";
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        var first = slash < 0 ? trimmed : trimmed[..slash];
        if (KnownFirstSegments.Contains(first)) return path;
        return slash < 0 ? "/<token>" : "/<token>" + trimmed[slash..];
    }
}
