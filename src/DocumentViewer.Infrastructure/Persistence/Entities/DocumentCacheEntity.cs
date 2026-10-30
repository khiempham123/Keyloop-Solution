namespace DocumentViewer.Infrastructure.Persistence.Entities;

public sealed class DocumentCacheEntity
{
    public string Vin { get; set; } = string.Empty;

    public short SourceSystem { get; set; }

    public string Payload { get; set; } = "[]";

    public int DocumentCount { get; set; }

    public DateTimeOffset FetchedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
