using DocumentViewer.Application.Aggregation;
using DocumentViewer.Application.Observability;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Domain;

namespace DocumentViewer.Infrastructure;

internal sealed class NullDocumentCacheRepository : IDocumentCacheRepository
{
    public Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct) =>
        Task.FromResult<CachedSourceDocuments?>(null);

    public Task UpsertAsync(
        Vin vin,
        SourceSystem system,
        IReadOnlyList<VehicleDocument> documents,
        TimeSpan ttl,
        CancellationToken ct) =>
        Task.CompletedTask;
}

internal sealed class NullSearchAuditRepository : ISearchAuditRepository
{
    public Task AppendAsync(SearchAuditEntry entry, CancellationToken ct) =>
        Task.CompletedTask;
}

internal sealed class NullAggregationMetrics : IAggregationMetrics
{
    public void RecordAggregation(string outcome, TimeSpan duration) { }

    public void RecordUpstream(SourceSystem system, SourceStatus status, TimeSpan duration) { }

    public void RecordPartial() { }

    public void RecordCacheLookup(string result) { }
}
