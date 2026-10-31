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
                SizeBytes: ToBytes(item.FileSizeKb),
                DownloadUrl: string.IsNullOrWhiteSpace(item.Url) ? null : item.Url.Trim()));
        }

        return normalized;
    }

    /// <summary>
    /// The Sales system reports size in kilobytes, so it has to be multiplied. An
    /// upstream is untrusted input (spec §2, principle 3): unchecked, a nonsense value
    /// wraps round and the API hands the client a <b>negative</b> byte count, which is
    /// worse than no number because a client will believe it. A size we cannot represent
    /// is reported as unknown, and the document is still returned.
    /// </summary>
    private const long MaxRepresentableKilobytes = long.MaxValue / 1024L;

    private static long? ToBytes(long? kilobytes) =>
        kilobytes is >= 0 and <= MaxRepresentableKilobytes
            ? kilobytes.Value * 1024L
            : null;

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
