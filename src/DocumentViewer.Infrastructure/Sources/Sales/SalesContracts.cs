namespace DocumentViewer.Infrastructure.Sources.Sales;

public sealed record SalesDocumentsResponse(IReadOnlyList<SalesDocumentItem?>? Documents);

public sealed record SalesDocumentItem(
    string? DocumentId,
    string? DocType,
    string? Description,
    string? CreatedDate,
    long? FileSizeKb,
    string? Url);
