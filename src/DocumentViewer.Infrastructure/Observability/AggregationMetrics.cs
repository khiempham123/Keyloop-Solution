using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Application.Observability;
using DocumentViewer.Domain;

namespace DocumentViewer.Infrastructure.Observability;

public sealed class AggregationMetrics : IAggregationMetrics, IDisposable
{
    private readonly Meter _meter;
    private readonly Histogram<double> _aggregationDuration;
    private readonly Histogram<double> _upstreamDuration;
    private readonly Counter<long> _upstreamResult;
    private readonly Counter<long> _partialResponses;
    private readonly Counter<long> _cacheLookups;
    private readonly ConcurrentDictionary<SourceSystem, int> _circuitStates = new()
    {
        [SourceSystem.SalesSystem] = 0,
        [SourceSystem.ServiceSystem] = 0
    };

    public AggregationMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory is not null
            ? meterFactory.Create(DocumentViewerTelemetry.MeterName)
            : new Meter(DocumentViewerTelemetry.MeterName, "1.0.0");

        _aggregationDuration = _meter.CreateHistogram<double>(
            "documentviewer.aggregation.duration",
            unit: "ms",
            description: "End-to-end document aggregation duration in milliseconds.");

        _upstreamDuration = _meter.CreateHistogram<double>(
            "documentviewer.upstream.duration",
            unit: "ms",
            description: "Per-upstream document fetch duration in milliseconds.");

        _upstreamResult = _meter.CreateCounter<long>(
            "documentviewer.upstream.result",
            description: "Per-upstream fetch outcome count tagged by system and status.");

        _partialResponses = _meter.CreateCounter<long>(
            "documentviewer.response.partial",
            description: "Number of responses served in a partial/degraded state.");

        _cacheLookups = _meter.CreateCounter<long>(
            "documentviewer.cache.lookup",
            description: "Per-source cache fallback lookup outcomes (hit, miss, error).");

        _meter.CreateObservableGauge<int>(
            "documentviewer.circuit.state",
            ObserveCircuitStates,
            description: "Circuit breaker state per upstream (0 = closed, 1 = open, 2 = half-open).");
    }

    public void RecordAggregation(string outcome, TimeSpan duration)
    {
        var tags = new TagList { { "outcome", outcome } };
        _aggregationDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordUpstream(SourceSystem system, SourceStatus status, TimeSpan duration)
    {
        var systemName = system.ToString();
        var statusName = status switch
        {
            SourceStatus.Ok => "ok",
            SourceStatus.Stale => "stale",
            SourceStatus.Failed => "failed",
            _ => "unknown"
        };

        var durationTags = new TagList { { "system", systemName } };
        _upstreamDuration.Record(duration.TotalMilliseconds, durationTags);

        var resultTags = new TagList
        {
            { "system", systemName },
            { "status", statusName }
        };
        _upstreamResult.Add(1, resultTags);
    }

    public void RecordPartial() => _partialResponses.Add(1);

    public void RecordCacheLookup(string result)
    {
        var tags = new TagList { { "result", result } };
        _cacheLookups.Add(1, tags);
    }

    public void SetCircuitState(SourceSystem system, int state) =>
        _circuitStates[system] = state;

    private IEnumerable<Measurement<int>> ObserveCircuitStates()
    {
        foreach (var (system, state) in _circuitStates)
        {
            yield return new Measurement<int>(
                state,
                new KeyValuePair<string, object?>("system", system.ToString()));
        }
    }

    public void Dispose() => _meter.Dispose();
}
