using System.Diagnostics;
using System.Text.RegularExpressions;
using DocumentViewer.Api.Endpoints;
using DocumentViewer.Application.Observability;
using DocumentViewer.Domain;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace DocumentViewer.Api.Observability;

public static partial class LoggingSetup
{
    public static WebApplicationBuilder AddObservabilityLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog(
            (context, services, loggerConfig) =>
            {
                loggerConfig
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                    .MinimumLevel.Override("Polly", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .Enrich.With(new CorrelationAndTraceLogEnricher())
                    .Enrich.With(new VinRedactingLogEnricher())
                    .WriteTo.Console(new RenderedCompactJsonFormatter());

                foreach (var sink in services.GetServices<ILogEventSink>())
                {
                    loggerConfig.WriteTo.Sink(sink);
                }
            },
            preserveStaticLogger: true);

        return builder;
    }

    public static WebApplication UseDocumentViewerRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod} {RoutePattern} responded {StatusCode} in {Elapsed:0.0000} ms";

            options.GetLevel = static (httpContext, _, ex) =>
            {
                if (httpContext.RequestAborted.IsCancellationRequested)
                {
                    return LogEventLevel.Information;
                }

                if (ex is not null)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Response.StatusCode == StatusCodes.Status503ServiceUnavailable)
                {
                    return LogEventLevel.Warning;
                }

                return httpContext.Response.StatusCode >= 500
                    ? LogEventLevel.Error
                    : LogEventLevel.Information;
            };

            options.EnrichDiagnosticContext = static (diagnosticContext, httpContext) =>
            {
                var routePattern = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                    ?? RedactVehicleSegment(httpContext.Request.Path.Value ?? "/");
                diagnosticContext.Set("RoutePattern", routePattern);

                if (httpContext.Items.TryGetValue(VehicleDocumentsEndpoint.CorrelationIdItemKey, out var corrObj) &&
                    corrObj is string correlationId)
                {
                    diagnosticContext.Set("CorrelationId", correlationId);
                }

                if (httpContext.Request.RouteValues.TryGetValue("vin", out var rawVinObj) &&
                    rawVinObj is string rawVin &&
                    Vin.TryParse(rawVin, out var parsedVin, out _))
                {
                    diagnosticContext.Set("VinHash", parsedVin.Hash12);
                    diagnosticContext.Set("VinMasked", parsedVin.Masked);
                }
            };
        });

        return app;
    }

    internal static string RedactVehicleSegment(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var pathScrubbed = VehiclePathRegex().Replace(input, "/vehicles/{vin}");
        return CandidateVinRegex().Replace(pathScrubbed, static match =>
            Vin.TryParse(match.Value, out var vin, out _) ? vin.Masked : match.Value);
    }

    [GeneratedRegex(@"/vehicles/[^/?#\s""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VehiclePathRegex();

    [GeneratedRegex(@"\b[A-HJ-NPR-Z0-9]{17}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CandidateVinRegex();

    private sealed class CorrelationAndTraceLogEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var correlationId = CorrelationContext.Current;
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CorrelationId", correlationId));
            }

            if (Activity.Current is { } activity)
            {
                if (activity.TraceId != default)
                {
                    logEvent.AddPropertyIfAbsent(
                        propertyFactory.CreateProperty("TraceId", activity.TraceId.ToString()));
                }

                if (activity.SpanId != default)
                {
                    logEvent.AddPropertyIfAbsent(
                        propertyFactory.CreateProperty("SpanId", activity.SpanId.ToString()));
                }
            }
        }
    }

    private sealed class VinRedactingLogEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            List<KeyValuePair<string, LogEventPropertyValue>>? updates = null;

            foreach (var (key, value) in logEvent.Properties)
            {
                if (TryScrubValue(value, out var scrubbedValue))
                {
                    updates ??= [];
                    updates.Add(new KeyValuePair<string, LogEventPropertyValue>(key, scrubbedValue));
                }
            }

            if (updates is null)
            {
                return;
            }

            foreach (var (key, scrubbedValue) in updates)
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(key, scrubbedValue));
            }
        }

        private static bool TryScrubValue(LogEventPropertyValue value, out LogEventPropertyValue scrubbed)
        {
            switch (value)
            {
                case ScalarValue { Value: string str } when str.Length >= 10:
                {
                    var redacted = RedactVehicleSegment(str);
                    if (!string.Equals(redacted, str, StringComparison.Ordinal))
                    {
                        scrubbed = new ScalarValue(redacted);
                        return true;
                    }

                    break;
                }

                case ScalarValue { Value: Uri uri }:
                {
                    var raw = uri.ToString();
                    var redacted = RedactVehicleSegment(raw);
                    if (!string.Equals(redacted, raw, StringComparison.Ordinal))
                    {
                        scrubbed = new ScalarValue(redacted);
                        return true;
                    }

                    break;
                }

                case StructureValue structure:
                {
                    var changed = false;
                    var props = new List<LogEventProperty>(structure.Properties.Count);
                    foreach (var prop in structure.Properties)
                    {
                        if (TryScrubValue(prop.Value, out var childScrubbed))
                        {
                            props.Add(new LogEventProperty(prop.Name, childScrubbed));
                            changed = true;
                        }
                        else
                        {
                            props.Add(prop);
                        }
                    }

                    if (changed)
                    {
                        scrubbed = new StructureValue(props, structure.TypeTag);
                        return true;
                    }

                    break;
                }

                case SequenceValue sequence:
                {
                    var changed = false;
                    var elements = new List<LogEventPropertyValue>(sequence.Elements.Count);
                    foreach (var element in sequence.Elements)
                    {
                        if (TryScrubValue(element, out var childScrubbed))
                        {
                            elements.Add(childScrubbed);
                            changed = true;
                        }
                        else
                        {
                            elements.Add(element);
                        }
                    }

                    if (changed)
                    {
                        scrubbed = new SequenceValue(elements);
                        return true;
                    }

                    break;
                }

                case DictionaryValue dictionary:
                {
                    var changed = false;
                    var pairs = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                    foreach (var (dictKey, dictVal) in dictionary.Elements)
                    {
                        if (TryScrubValue(dictVal, out var childScrubbed))
                        {
                            pairs.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(dictKey, childScrubbed));
                            changed = true;
                        }
                        else
                        {
                            pairs.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(dictKey, dictVal));
                        }
                    }

                    if (changed)
                    {
                        scrubbed = new DictionaryValue(pairs);
                        return true;
                    }

                    break;
                }
            }

            scrubbed = value;
            return false;
        }
    }
}
