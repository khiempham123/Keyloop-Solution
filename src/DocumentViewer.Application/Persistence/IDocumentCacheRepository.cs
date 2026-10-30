using DocumentViewer.Domain;

namespace DocumentViewer.Application.Persistence;

public sealed record CachedSourceDocuments(
    IReadOnlyList<VehicleDocument> Documents,
    DateTimeOffset FetchedAt);

public interface IDocumentCacheRepository
{
    Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct);

    Task UpsertAsync(
        Vin vin,
        SourceSystem system,
        IReadOnlyList<VehicleDocument> documents,
        TimeSpan ttl,
        CancellationToken ct);
}
