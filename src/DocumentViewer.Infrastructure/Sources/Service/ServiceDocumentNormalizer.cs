using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;

namespace DocumentViewer.Infrastructure.Sources.Service;

public sealed class ServiceDocumentNormalizer : IDocumentNormalizer<ServiceDocumentsResponse>
{
    public IReadOnlyList<VehicleDocument> Normalize(ServiceDocumentsResponse? payload)
    {
        if (payload?.Data?.Items is not { Count: > 0 } items)
        {
            return [];
        }

        var seenIds = new HashSet<long>();
        var normalized = new List<VehicleDocument>(items.Count);

        foreach (var item in items)
        {
            if (item is null ||
                item.Id is null ||
                string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            if (!seenIds.Add(item.Id.Value))
            {
                continue;
            }

            normalized.Add(new VehicleDocument(
                Id: $"service:{item.Id.Value}",
                SourceSystem: SourceSystem.ServiceSystem,
                DocumentType: MapCategory(item.Category),
                Title: item.Name.Trim(),
                IssuedAt: ParseEpochMillis(item.Timestamp),
                SizeBytes: item.Bytes is >= 0 ? item.Bytes.Value : null,
                DownloadUrl: string.IsNullOrWhiteSpace(item.Links?.Self) ? null : item.Links.Self.Trim()));
        }

        return normalized;
    }

    private static DocumentType MapCategory(string? category) =>
        category?.Trim().ToLowerInvariant() switch
        {
            "job-card" => DocumentType.JobCard,
            "inspection-report" => DocumentType.InspectionReport,
            "warranty-claim" => DocumentType.WarrantyClaim,
            "purchase-order" => DocumentType.PurchaseOrder,
            "finance-agreement" => DocumentType.FinanceAgreement,
            "registration-document" => DocumentType.RegistrationDocument,
            _ => DocumentType.Unknown
        };

    private static DateTimeOffset? ParseEpochMillis(long? epochMillis)
    {
        if (epochMillis is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epochMillis.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
