using DocumentViewer.Application.Aggregation;
using DocumentViewer.Domain;

namespace DocumentViewer.Application.Observability;

public interface IAggregationMetrics
{
    void RecordAggregation(string outcome, TimeSpan duration);

    void RecordUpstream(SourceSystem system, SourceStatus status, TimeSpan duration);

    void RecordPartial();

    void RecordCacheLookup(string result);
}
