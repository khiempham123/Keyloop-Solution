namespace MockServiceApi;

public sealed record ServiceLinksDto(string Self);

public sealed record ServiceItemDto(
    int Id,
    string Category,
    string Name,
    long Timestamp,
    long Bytes,
    ServiceLinksDto Links);

public sealed record ServiceDataDto(IReadOnlyList<ServiceItemDto> Items);

public sealed record ServiceMetaDto(int Count);

public sealed record ServiceDocumentsEnvelope(ServiceDataDto Data, ServiceMetaDto Meta);

public static class SeedData
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ServiceItemDto>> ByVin =
        new Dictionary<string, IReadOnlyList<ServiceItemDto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1HGCM82633A004352"] =
            [
                new(
                    Id: 88213,
                    Category: "job-card",
                    Name: "Job card #88213 — annual service",
                    Timestamp: 1772582400000L, // 2026-03-04T00:00:00Z
                    Bytes: 148213L,
                    Links: new ServiceLinksDto("https://service.mock/docs/88213")),
                new(
                    Id: 91044,
                    Category: "inspection-report",
                    Name: "Multi-point inspection report #91044",
                    Timestamp: 1773014400000L, // 2026-03-09T00:00:00Z
                    Bytes: 524900L,
                    Links: new ServiceLinksDto("https://service.mock/docs/91044")),
                new(
                    Id: 95501,
                    Category: "recall-bulletin", // deliberately unrecognised category -> maps to DocumentType.Unknown
                    Name: "Manufacturer campaign check #95501",
                    Timestamp: 1773532800000L, // 2026-03-15T00:00:00Z
                    Bytes: 87412L,
                    Links: new ServiceLinksDto("https://service.mock/docs/95501"))
            ],
            ["WAUZZZ8V1JA003421"] = [],
            ["WAUZZZ8V7JA003421"] = []
        };

    public static IReadOnlyList<ServiceItemDto> GetForVin(string vin) =>
        ByVin.TryGetValue(vin.Trim(), out var items) ? items : [];
}
