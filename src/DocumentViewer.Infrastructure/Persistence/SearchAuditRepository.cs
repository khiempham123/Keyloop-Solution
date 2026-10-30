using System.Text.Json;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Domain;
using DocumentViewer.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocumentViewer.Infrastructure.Persistence;

public sealed class SearchAuditRepository(
    IDbContextFactory<DocumentViewerDbContext> dbFactory,
    ILogger<SearchAuditRepository> logger) : ISearchAuditRepository
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task AppendAsync(SearchAuditEntry entry, CancellationToken ct)
    {
        using var dbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dbCts.CancelAfter(OperationTimeout);

        try
        {
            var entity = new SearchAuditEntity
            {
                Vin = entry.Vin,
                CorrelationId = Truncate(entry.CorrelationId, 128),
                SearchedAt = entry.SearchedAt.ToUniversalTime(),
                DurationMs = entry.DurationMs,
                Partial = entry.Partial,
                SourceReport = JsonSerializer.Serialize(entry.Sources, SerializerOptions)
            };

            await using var db = await dbFactory.CreateDbContextAsync(dbCts.Token);
            db.SearchAudits.Add(entity);
            await db.SaveChangesAsync(dbCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (vinHash, vinMasked) = RedactVin(entry.Vin);
            logger.LogWarning(
                ex,
                "Search audit write failed for correlation {CorrelationId} and VIN {VinHash} ({VinMasked}); swallowing failure",
                entry.CorrelationId,
                vinHash,
                vinMasked);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static (string Hash12, string Masked) RedactVin(string rawVin) =>
        Vin.TryParse(rawVin, out var parsed, out _)
            ? (parsed.Hash12, parsed.Masked)
            : ("invalid", "***");
}
