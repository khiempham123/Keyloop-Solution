using DocumentViewer.Api.Contracts;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Domain;

namespace DocumentViewer.Api.Endpoints;

public static class VehicleDocumentsEndpoint
{
    private static readonly TimeSpan WholeRequestBudget = TimeSpan.FromSeconds(6);

    public static IEndpointRouteBuilder MapVehicleDocumentsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet("/api/v1/vehicles/{vin}/documents", HandleAsync)
            .WithName("GetVehicleDocuments")
            .WithSummary("Returns a consolidated, source-attributed document list for a vehicle by VIN.")
            .Produces<DocumentsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        string vin,
        IDocumentAggregationService aggregation,
        HttpContext httpContext)
    {
        if (!Vin.TryParse(vin, out var parsedVin, out var vinError))
        {
            return TypedResults.Problem(
                title: "Invalid Vehicle Identification Number (VIN).",
                detail: DescribeVinError(vinError),
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://keyloop.example/problems/invalid-vin");
        }

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
        budgetCts.CancelAfter(WholeRequestBudget);

        AggregationResult result;
        try
        {
            result = await aggregation.GetDocumentsAsync(parsedVin, budgetCts.Token);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (OperationCanceledException)
        {
            httpContext.Response.Headers.RetryAfter = "30";
            return TypedResults.Problem(
                title: "Request timed out waiting for upstream document systems.",
                detail: "The 6-second whole-request timeout budget elapsed before any source produced a usable response.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "https://keyloop.example/problems/request-timeout");
        }

        if (result.AllSourcesFailed)
        {
            httpContext.Response.Headers.RetryAfter = "30";
            return TypedResults.Problem(
                title: "All upstream document sources failed.",
                detail: "Neither Sales nor Service document systems responded successfully, and no cached documents were available.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "https://keyloop.example/problems/upstreams-unavailable");
        }

        return TypedResults.Ok(MapResponse(result));
    }

    private static DocumentsResponse MapResponse(AggregationResult result) =>
        new(
            Vin: result.Vin.Value,
            RetrievedAt: result.RetrievedAt,
            Partial: result.Partial,
            Documents: result.Documents
                .Select(static d => new DocumentItemResponse(
                    d.Id,
                    d.SourceSystem,
                    d.DocumentType,
                    d.Title,
                    d.IssuedAt,
                    d.SizeBytes,
                    d.DownloadUrl))
                .ToList(),
            Sources: result.Sources
                .Select(static s => new SourceItemResponse(
                    s.System,
                    s.Status,
                    s.DocumentCount,
                    s.DurationMs,
                    s.Error is null
                        ? null
                        : new SourceErrorResponse(s.Error.Reason, s.Error.Message, s.Error.StatusCode)))
                .ToList());

    private static string DescribeVinError(VinError error) => error switch
    {
        VinError.Empty => "VIN must not be empty.",
        VinError.InvalidLength => "VIN must be exactly 17 characters after trimming whitespace.",
        VinError.IllegalCharacter => "VIN contains illegal characters (only 0-9 and A-Z excluding I, O, and Q are permitted).",
        VinError.InvalidCheckDigit => "VIN failed ISO 3779 check-digit validation at position 9.",
        _ => "VIN is invalid."
    };
}
