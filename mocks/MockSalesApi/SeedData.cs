namespace MockSalesApi;

public sealed record SalesDocumentDto(
    string DocumentId,
    string DocType,
    string Description,
    string CreatedDate,
    int FileSizeKb,
    string Url);

public sealed record SalesDocumentsEnvelope(IReadOnlyList<SalesDocumentDto> Documents);

public static class SeedData
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SalesDocumentDto>> ByVin =
        new Dictionary<string, IReadOnlyList<SalesDocumentDto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1HGCM82633A004352"] =
            [
                new(
                    DocumentId: "PO-88213",
                    DocType: "PURCHASE_ORDER",
                    Description: "Purchase order PO-88213",
                    CreatedDate: "2026-03-04",
                    FileSizeKb: 145,
                    Url: "https://sales.mock/documents/PO-88213"),
                new(
                    DocumentId: "FA-44102",
                    DocType: "FINANCE_AGREEMENT",
                    Description: "Retail finance agreement FA-44102",
                    CreatedDate: "2026-03-05",
                    FileSizeKb: 312,
                    Url: "https://sales.mock/documents/FA-44102"),
                new(
                    DocumentId: "REG-99101",
                    DocType: "REGISTRATION_DOCUMENT",
                    Description: "Vehicle registration certificate REG-99101",
                    CreatedDate: "2026-03-10",
                    FileSizeKb: 98,
                    Url: "https://sales.mock/documents/REG-99101"),
                new(
                    DocumentId: "WAR-12008",
                    DocType: "EXTENDED_WARRANTY_PACK",
                    Description: "Dealer extended protection schedule WAR-12008",
                    CreatedDate: "2026-03-12",
                    FileSizeKb: 220,
                    Url: "https://sales.mock/documents/WAR-12008")
            ],
            ["WAUZZZ8V1JA003421"] = [],
            ["WAUZZZ8V7JA003421"] = []
        };

    public static IReadOnlyList<SalesDocumentDto> GetForVin(string vin) =>
        ByVin.TryGetValue(vin.Trim(), out var docs) ? docs : [];
}
