using System.Diagnostics;

namespace DocumentViewer.Application.Observability;

public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    public static string? Current
    {
        get => CurrentId.Value;
        set => CurrentId.Value = value;
    }
}

public static class DocumentViewerTelemetry
{
    public const string ServiceName = "DocumentViewer";
    public const string ActivitySourceName = "DocumentViewer";
    public const string MeterName = "DocumentViewer";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
}
