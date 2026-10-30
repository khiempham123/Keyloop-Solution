using System.Text.Json.Serialization;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Domain;

namespace DocumentViewer.Api.Contracts;

public sealed record DocumentsResponse(
    string Vin,
    DateTimeOffset RetrievedAt,
    bool Partial,
    IReadOnlyList<DocumentItemResponse> Documents,
    IReadOnlyList<SourceItemResponse> Sources);

public sealed record DocumentItemResponse(
    string Id,
    SourceSystem SourceSystem,
    DocumentType DocumentType,
    string Title,
    DateTimeOffset? IssuedAt,
    long? SizeBytes,
    string? DownloadUrl);

public sealed record SourceItemResponse(
    SourceSystem System,
    SourceStatus Status,
    int DocumentCount,
    long DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SourceErrorResponse? Error);

public sealed record SourceErrorResponse(
    UpstreamFailureReason Reason,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? StatusCode);
