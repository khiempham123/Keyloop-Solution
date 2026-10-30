namespace DocumentViewer.Domain;

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
