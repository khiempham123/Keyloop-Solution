namespace DocumentViewer.Infrastructure.Persistence.Entities;

public sealed class SearchAuditEntity
{
    public long Id { get; set; }

    public string Vin { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public DateTimeOffset SearchedAt { get; set; }

    public int DurationMs { get; set; }

    public bool Partial { get; set; }

    public string SourceReport { get; set; } = "[]";
}
