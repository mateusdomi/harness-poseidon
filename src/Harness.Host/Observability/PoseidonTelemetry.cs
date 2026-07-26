using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Harness.Host.Observability;

internal static class PoseidonTelemetry
{
    internal const string ServiceName = "poseidon-host";
    internal const string ActivitySourceName = "Poseidon";
    internal const string MeterName = "Poseidon";

    internal static IServiceCollection AddPoseidonTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var telemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(ActivitySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddProcessor(new SensitiveTelemetryProcessor());

                if (HasOtlpEndpoint(configuration, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (HasOtlpEndpoint(configuration, "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"))
                {
                    metrics.AddOtlpExporter();
                }
            });

        return services;
    }

    internal static bool HasOtlpEndpoint(
        IConfiguration configuration,
        string signalEndpointKey)
    {
        var endpoint = configuration[signalEndpointKey];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && string.IsNullOrEmpty(uri.UserInfo);
    }
}

internal sealed class SensitiveTelemetryProcessor : BaseProcessor<Activity>
{
    private static readonly string[] SensitiveFragments =
    [
        "authorization",
        "cookie",
        "email",
        "exception.message",
        "exception.stacktrace",
        "http.request.body",
        "http.request.header",
        "http.response.body",
        "http.response.header",
        "http.target",
        "http.url",
        "password",
        "prompt",
        "request.body",
        "response.body",
        "secret",
        "token",
        "url.full",
        "url.query",
        "user.",
    ];

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in activity.TagObjects.ToArray())
        {
            if (IsSensitive(tag.Key))
            {
                activity.SetTag(tag.Key, null);
            }
        }
    }

    private static bool IsSensitive(string key) =>
        SensitiveFragments.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
