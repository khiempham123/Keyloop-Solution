using DocumentViewer.Domain;

namespace DocumentViewer.Application.Sources;

public interface IDocumentSource
{
    SourceSystem System { get; }

    Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct);
}

public interface IDocumentNormalizer<in TPayload>
{
    IReadOnlyList<VehicleDocument> Normalize(TPayload payload);
}
