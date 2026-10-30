using DocumentViewer.Application.Aggregation;

namespace DocumentViewer.Application.Persistence;

public sealed record SearchAuditEntry(
    string Vin,
    string CorrelationId,
    DateTimeOffset SearchedAt,
    int DurationMs,
    bool Partial,
    IReadOnlyList<SourceReport> Sources);

public interface ISearchAuditRepository
{
    Task AppendAsync(SearchAuditEntry entry, CancellationToken ct);
}
