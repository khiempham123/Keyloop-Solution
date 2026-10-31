using System.Diagnostics;
using DocumentViewer.Application.Observability;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DocumentViewer.Api.Observability;

public static class TelemetrySetup
{
    public static WebApplicationBuilder AddObservabilityTelemetry(this WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var hasOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);
        var useConsoleFallback = !string.Equals(
            builder.Configuration["Observability:UseConsoleExporter"],
            "false",
            StringComparison.OrdinalIgnoreCase);

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(static resource =>
                resource.AddService(
                    serviceName: DocumentViewerTelemetry.ServiceName,
                    serviceVersion: "1.0.0"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(DocumentViewerTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(static options =>
                    {
                        options.RecordException = true;
                    })
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddProcessor(new VinRedactingActivityProcessor());

                if (hasOtlp)
                {
                    tracing.AddOtlpExporter();
                }
                else if (useConsoleFallback)
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(DocumentViewerTelemetry.MeterName);

                if (hasOtlp)
                {
                    metrics.AddOtlpExporter();
                }
                else if (useConsoleFallback)
                {
                    metrics.AddConsoleExporter();
                }
            });

        return builder;
    }

    private sealed class VinRedactingActivityProcessor : BaseProcessor<Activity>
    {
        private static readonly string[] UrlTagKeys =
        [
            "url.path",
            "url.full",
            "http.target",
            "http.url"
        ];

        public override void OnEnd(Activity activity)
        {
            foreach (var key in UrlTagKeys)
            {
                if (activity.GetTagItem(key) is string raw && raw.Length >= 10)
                {
                    var scrubbed = LoggingSetup.RedactVehicleSegment(raw);
                    if (!string.Equals(scrubbed, raw, StringComparison.Ordinal))
                    {
                        activity.SetTag(key, scrubbed);
                    }
                }
            }

            if (!string.IsNullOrEmpty(activity.DisplayName) && activity.DisplayName.Length >= 10)
            {
                var scrubbedName = LoggingSetup.RedactVehicleSegment(activity.DisplayName);
                if (!string.Equals(scrubbedName, activity.DisplayName, StringComparison.Ordinal))
                {
                    activity.DisplayName = scrubbedName;
                }
            }
        }
    }
}
