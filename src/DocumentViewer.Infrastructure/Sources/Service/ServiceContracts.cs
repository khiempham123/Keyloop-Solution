namespace DocumentViewer.Infrastructure.Sources.Service;

public sealed record ServiceDocumentsResponse(
    ServiceDataPayload? Data,
    ServiceMetaPayload? Meta);

public sealed record ServiceDataPayload(IReadOnlyList<ServiceDocumentItem?>? Items);

public sealed record ServiceMetaPayload(int? Count);

public sealed record ServiceLinksPayload(string? Self);

public sealed record ServiceDocumentItem(
    long? Id,
    string? Category,
    string? Name,
    long? Timestamp,
    long? Bytes,
    ServiceLinksPayload? Links);
