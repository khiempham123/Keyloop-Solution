using System.Text.Json.Serialization;

namespace DocumentViewer.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpstreamFailureReason
{
    Timeout,
    CircuitOpen,
    HttpError,
    InvalidPayload,
    Unknown
}

public sealed record UpstreamFailure(
    UpstreamFailureReason Reason,
    string Message,
    int? StatusCode);
