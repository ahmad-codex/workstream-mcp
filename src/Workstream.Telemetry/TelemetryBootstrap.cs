using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Workstream.Telemetry;

/// <summary>
/// Wires OTel resource, propagators, and exporters (§11.2). The exporter target is
/// configurable via <c>WORKSTREAM_OTEL_ENDPOINT</c>; absent that, OTel is wired with a
/// no-op exporter so dev runs stay quiet.
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
                b.AddAspNetCoreInstrumentation();
                b.AddHttpClientInstrumentation();
                if (!string.IsNullOrEmpty(otelEndpoint))
                    b.AddOtlpExporter(o => o.Endpoint = new System.Uri(otelEndpoint));
            });

        return services;
    }
}
