using System.Text.Json;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocumentViewer.Infrastructure.Persistence;

public sealed class DocumentCacheRepository(
    IDbContextFactory<DocumentViewerDbContext> dbFactory,
    TimeProvider clock,
    ILogger<DocumentCacheRepository> logger) : IDocumentCacheRepository
{
    /// <summary>
    /// A read sits on the response path, so it gets a short budget and a cache miss is
    /// an acceptable outcome.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// A write happens after the response has been sent, so it gets room for a cold
    /// cross-region connection. 1500ms was too tight for a managed pooler's
    /// TLS + tenant-lookup + SASL handshake, which meant the cache stayed permanently
    /// empty and the stale fallback never worked.
    /// </summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct)
    {
        using var dbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dbCts.CancelAfter(ReadTimeout);

        try
        {
            var now = clock.GetUtcNow().ToUniversalTime();
            var sourceCode = (short)system;

            await using var db = await dbFactory.CreateDbContextAsync(dbCts.Token);
            var row = await db.DocumentCaches
                .AsNoTracking()
                .Where(c => c.Vin == vin.Value && c.SourceSystem == sourceCode && c.ExpiresAt > now)
                .FirstOrDefaultAsync(dbCts.Token);

            if (row is null)
            {
                return null;
            }

            var documents = JsonSerializer.Deserialize<List<VehicleDocument>>(row.Payload, SerializerOptions);
            return documents is null ? null : new CachedSourceDocuments(documents, row.FetchedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Cache read failed for source {SourceSystem} and VIN {VinHash} ({VinMasked}); returning cache miss",
                system,
                vin.Hash12,
                vin.Masked);
            return null;
        }
    }

    public async Task UpsertAsync(
        Vin vin,
        SourceSystem system,
        IReadOnlyList<VehicleDocument> documents,
        TimeSpan ttl,
        CancellationToken ct)
    {
        using var dbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dbCts.CancelAfter(WriteTimeout);

        try
        {
            var now = clock.GetUtcNow().ToUniversalTime();
            var expiresAt = now.Add(ttl).ToUniversalTime();
            var payloadJson = JsonSerializer.Serialize(documents, SerializerOptions);
            var sourceCode = (short)system;
            var documentCount = documents.Count;
            var vinValue = vin.Value;

            await using var db = await dbFactory.CreateDbContextAsync(dbCts.Token);
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO document_metadata_cache (vin, source_system, payload, document_count, fetched_at, expires_at)
                 VALUES ({vinValue}, {sourceCode}, CAST({payloadJson} AS jsonb), {documentCount}, {now}, {expiresAt})
                 ON CONFLICT (vin, source_system) DO UPDATE
                 SET payload = EXCLUDED.payload,
                     document_count = EXCLUDED.document_count,
                     fetched_at = EXCLUDED.fetched_at,
                     expires_at = EXCLUDED.expires_at;
                 """,
                dbCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Cache upsert failed for source {SourceSystem} and VIN {VinHash} ({VinMasked}); continuing without failing read",
                system,
                vin.Hash12,
                vin.Masked);
        }
    }
}
