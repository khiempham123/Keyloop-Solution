using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using Microsoft.Extensions.Logging;

namespace DocumentViewer.Infrastructure.Sources.Service;

public sealed class ServiceSystemClient(
    HttpClient httpClient,
    IDocumentNormalizer<ServiceDocumentsResponse> normalizer,
    ILogger<ServiceSystemClient> logger) : IDocumentSource
{
    public SourceSystem System => SourceSystem.ServiceSystem;

    public Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct) =>
        UpstreamClientExecutor.ExecuteAsync(
            httpClient,
            $"/service/v2/vehicles/{Uri.EscapeDataString(vin.Value)}/docs",
            System,
            vin,
            normalizer,
            logger,
            ct);
}
