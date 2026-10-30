using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using Microsoft.Extensions.Logging;

namespace DocumentViewer.Infrastructure.Sources.Sales;

public sealed class SalesSystemClient(
    HttpClient httpClient,
    IDocumentNormalizer<SalesDocumentsResponse> normalizer,
    ILogger<SalesSystemClient> logger) : IDocumentSource
{
    public SourceSystem System => SourceSystem.SalesSystem;

    public Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct) =>
        UpstreamClientExecutor.ExecuteAsync(
            httpClient,
            $"/sales/vehicles/{Uri.EscapeDataString(vin.Value)}/documents",
            System,
            vin,
            normalizer,
            logger,
            ct);
}
