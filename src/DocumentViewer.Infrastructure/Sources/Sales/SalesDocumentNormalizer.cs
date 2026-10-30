using System.Globalization;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;

namespace DocumentViewer.Infrastructure.Sources.Sales;

public sealed class SalesDocumentNormalizer : IDocumentNormalizer<SalesDocumentsResponse>
{
    public IReadOnlyList<VehicleDocument> Normalize(SalesDocumentsResponse? payload)
    {
        if (payload?.Documents is not { Count: > 0 } items)
        {
            return [];
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<VehicleDocument>(items.Count);

        foreach (var item in items)
        {
            if (item is null ||
                string.IsNullOrWhiteSpace(item.DocumentId) ||
                string.IsNullOrWhiteSpace(item.Description))
            {
                continue;
            }

            var externalId = item.DocumentId.Trim();
            if (!seenIds.Add(externalId))
            {
                continue;
            }

            normalized.Add(new VehicleDocument(
                Id: $"sales:{externalId}",
                SourceSystem: SourceSystem.SalesSystem,
                DocumentType: MapDocType(item.DocType),
                Title: item.Description.Trim(),
                IssuedAt: ParseIsoDateUtc(item.CreatedDate),
                SizeBytes: item.FileSizeKb is >= 0 ? item.FileSizeKb.Value * 1024L : null,
                DownloadUrl: string.IsNullOrWhiteSpace(item.Url) ? null : item.Url.Trim()));
        }

        return normalized;
    }

    private static DocumentType MapDocType(string? docType) =>
        docType?.Trim().ToUpperInvariant() switch
        {
            "PURCHASE_ORDER" => DocumentType.PurchaseOrder,
            "FINANCE_AGREEMENT" => DocumentType.FinanceAgreement,
            "REGISTRATION_DOCUMENT" => DocumentType.RegistrationDocument,
            "JOB_CARD" => DocumentType.JobCard,
            "INSPECTION_REPORT" => DocumentType.InspectionReport,
            "WARRANTY_CLAIM" => DocumentType.WarrantyClaim,
            _ => DocumentType.Unknown
        };

    private static DateTimeOffset? ParseIsoDateUtc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            return new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
