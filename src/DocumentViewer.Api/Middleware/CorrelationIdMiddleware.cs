using System.Diagnostics;
using DocumentViewer.Api.Endpoints;
using DocumentViewer.Application.Observability;

namespace DocumentViewer.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext httpContext)
    {
        var correlationId = ResolveInboundCorrelationId(httpContext);
        var previous = CorrelationContext.Current;

        CorrelationContext.Current = correlationId;
        httpContext.Items[VehicleDocumentsEndpoint.CorrelationIdItemKey] = correlationId;
        httpContext.TraceIdentifier = correlationId;
        httpContext.Response.Headers[HeaderName] = correlationId;

        httpContext.Response.OnStarting(() =>
        {
            httpContext.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        try
        {
            await next(httpContext);
        }
        finally
        {
            CorrelationContext.Current = previous;
        }
    }

    private static string ResolveInboundCorrelationId(HttpContext httpContext)
    {
        if (httpContext.Request.Headers.TryGetValue("traceparent", out var traceparentValues))
        {
            var rawTraceparent = traceparentValues.ToString().Trim();
            if (ActivityContext.TryParse(rawTraceparent, null, out var parsedContext) &&
                parsedContext.TraceId != default)
            {
                return parsedContext.TraceId.ToString();
            }
        }

        if (httpContext.Request.Headers.TryGetValue(HeaderName, out var correlationValues))
        {
            var inbound = correlationValues.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(inbound))
            {
                return inbound.Length <= 128 ? inbound : inbound[..128];
            }
        }

        if (Activity.Current?.TraceId is { } activeTraceId && activeTraceId != default)
        {
            return activeTraceId.ToString();
        }

        return Guid.NewGuid().ToString("N");
    }
}
