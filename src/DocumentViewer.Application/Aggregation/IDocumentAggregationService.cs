using DocumentViewer.Domain;

namespace DocumentViewer.Application.Aggregation;

public interface IDocumentAggregationService
{
    Task<AggregationResult> GetDocumentsAsync(Vin vin, CancellationToken ct);
}
