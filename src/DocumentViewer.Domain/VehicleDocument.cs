namespace DocumentViewer.Domain;

public sealed record VehicleDocument(
    string Id,
    SourceSystem SourceSystem,
    DocumentType DocumentType,
    string Title,
    DateTimeOffset? IssuedAt,
    long? SizeBytes,
    string? DownloadUrl);
