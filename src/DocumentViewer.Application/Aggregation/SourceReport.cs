using System.Text.Json.Serialization;
using DocumentViewer.Domain;

namespace DocumentViewer.Application.Aggregation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceStatus
{
    [JsonStringEnumMemberName("ok")]
    Ok,

    [JsonStringEnumMemberName("stale")]
    Stale,

    [JsonStringEnumMemberName("failed")]
    Failed
}

public sealed record SourceReport(
    SourceSystem System,
    SourceStatus Status,
    int DocumentCount,
    long DurationMs,
    UpstreamFailure? Error);
