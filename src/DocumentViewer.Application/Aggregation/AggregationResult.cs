using DocumentViewer.Domain;

namespace DocumentViewer.Application.Aggregation;

public sealed record AggregationResult(
    Vin Vin,
    DateTimeOffset RetrievedAt,
    bool Partial,
    IReadOnlyList<VehicleDocument> Documents,
    IReadOnlyList<SourceReport> Sources)
{
    public bool AllSourcesFailed => Sources.Count > 0 && Sources.All(static s => s.Status == SourceStatus.Failed);
}
