using System.Diagnostics;
using DocumentViewer.Application.Observability;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using Microsoft.Extensions.Logging;

namespace DocumentViewer.Application.Aggregation;

public sealed class DocumentAggregationService(
    IEnumerable<IDocumentSource> sources,
    IDocumentCacheRepository cache,
    IAggregationMetrics metrics,
    TimeProvider clock,
    ILogger<DocumentAggregationService> logger) : IDocumentAggregationService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the response path is willing to wait for the cache. Beyond this the
    /// read completes and the cache operation is left to finish on its own.
    /// </summary>
    private static readonly TimeSpan CacheOperationBudget = TimeSpan.FromSeconds(1);

    public async Task<AggregationResult> GetDocumentsAsync(Vin vin, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var sourceList = sources as IReadOnlyList<IDocumentSource> ?? sources.ToList();

        var fanOutTasks = new Task<(SourceSystem System, UpstreamResult<IReadOnlyList<VehicleDocument>> Outcome)>[sourceList.Count];
        for (var i = 0; i < sourceList.Count; i++)
        {
            fanOutTasks[i] = FetchSafelyAsync(sourceList[i], vin, ct);
        }

        var outcomes = await Task.WhenAll(fanOutTasks);

        var mergedDocuments = new List<VehicleDocument>();
        var reports = new List<SourceReport>(outcomes.Length);
        var cacheUpsertTasks = new List<Task>(outcomes.Length);

        foreach (var (system, outcome) in outcomes)
        {
            switch (outcome)
            {
                case UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok ok:
                    mergedDocuments.AddRange(ok.Value);
                    reports.Add(new SourceReport(
                        System: system,
                        Status: SourceStatus.Ok,
                        DocumentCount: ok.Value.Count,
                        DurationMs: (long)ok.Duration.TotalMilliseconds,
                        Error: null));

                    metrics.RecordUpstream(system, SourceStatus.Ok, ok.Duration);
                    cacheUpsertTasks.Add(TryCacheUpsertAsync(vin, system, ok.Value));
                    break;

                case UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed failed:
                    var cached = await TryCacheGetWithinBudgetAsync(vin, system, ct);
                    if (cached is not null)
                    {
                        mergedDocuments.AddRange(cached.Documents);
                        reports.Add(new SourceReport(
                            System: system,
                            Status: SourceStatus.Stale,
                            DocumentCount: cached.Documents.Count,
                            DurationMs: (long)failed.Duration.TotalMilliseconds,
                            Error: failed.Failure));

                        metrics.RecordUpstream(system, SourceStatus.Stale, failed.Duration);
                    }
                    else
                    {
                        reports.Add(new SourceReport(
                            System: system,
                            Status: SourceStatus.Failed,
                            DocumentCount: 0,
                            DurationMs: (long)failed.Duration.TotalMilliseconds,
                            Error: failed.Failure));

                        metrics.RecordUpstream(system, SourceStatus.Failed, failed.Duration);
                    }
                    break;
            }
        }

        if (cacheUpsertTasks.Count > 0)
        {
            // Cache writes are an optimisation, never a reason to make a user wait. A
            // database that accepts the connection and then never answers used to hold
            // the whole read until the request budget expired.
            var writes = Task.WhenAll(cacheUpsertTasks);
            if (await Task.WhenAny(writes, Task.Delay(CacheOperationBudget)) != writes)
            {
                logger.LogWarning(
                    "Cache writes for VIN {VinHash} ({VinMasked}) exceeded {BudgetMs}ms; "
                    + "completing the read without waiting for them",
                    vin.Hash12,
                    vin.Masked,
                    CacheOperationBudget.TotalMilliseconds);
                Forget(writes);
            }
        }

        var orderedDocuments = mergedDocuments
            .OrderByDescending(static d => d.IssuedAt.HasValue)
            .ThenByDescending(static d => d.IssuedAt)
            .ThenBy(static d => d.Id, StringComparer.Ordinal)
            .ToList();

        var partial = reports.Any(static r => r.Status != SourceStatus.Ok);
        var result = new AggregationResult(
            Vin: vin,
            RetrievedAt: clock.GetUtcNow().ToUniversalTime(),
            Partial: partial,
            Documents: orderedDocuments,
            Sources: reports);

        var totalDuration = Stopwatch.GetElapsedTime(startedAt);
        if (partial)
        {
            metrics.RecordPartial();
        }

        var outcomeTag = result.AllSourcesFailed
            ? "failed"
            : partial
                ? "partial"
                : "ok";
        metrics.RecordAggregation(outcomeTag, totalDuration);

        return result;
    }

    private async Task<(SourceSystem System, UpstreamResult<IReadOnlyList<VehicleDocument>> Outcome)> FetchSafelyAsync(
        IDocumentSource source,
        Vin vin,
        CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var outcome = await source.FetchAsync(vin, ct);
            return (source.System, outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            logger.LogWarning(
                ex,
                "Source {SourceSystem} threw unexpectedly despite contract for VIN {VinHash} ({VinMasked})",
                source.System,
                vin.Hash12,
                vin.Masked);

            var failed = new UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed(
                new UpstreamFailure(UpstreamFailureReason.Unknown, ex.Message, null),
                elapsed);
            return (source.System, failed);
        }
    }

    private async Task<CachedSourceDocuments?> TryCacheGetWithinBudgetAsync(
        Vin vin,
        SourceSystem system,
        CancellationToken ct)
    {
        var lookup = TryCacheGetAsync(vin, system, ct);
        if (await Task.WhenAny(lookup, Task.Delay(CacheOperationBudget)) != lookup)
        {
            logger.LogWarning(
                "Cache lookup for source {SourceSystem} and VIN {VinHash} ({VinMasked}) exceeded "
                + "{BudgetMs}ms; treating as a cache miss",
                system,
                vin.Hash12,
                vin.Masked,
                CacheOperationBudget.TotalMilliseconds);
            Forget(lookup);
            return null;
        }

        return await lookup;
    }

    private static void Forget(Task task) =>
        task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

    private async Task<CachedSourceDocuments?> TryCacheGetAsync(Vin vin, SourceSystem system, CancellationToken ct)
    {
        try
        {
            var cached = await cache.GetAsync(vin, system, ct);
            metrics.RecordCacheLookup(cached is not null ? "hit" : "miss");
            return cached;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordCacheLookup("error");
            logger.LogWarning(
                ex,
                "Cache lookup failed for source {SourceSystem} and VIN {VinHash} ({VinMasked}); treating as cache miss",
                system,
                vin.Hash12,
                vin.Masked);
            return null;
        }
    }

    /// <summary>
    /// Writes the cache deliberately WITHOUT the request's cancellation token. That
    /// token is cancelled the moment the response is sent, and a cross-region write
    /// takes longer than the request's remaining lifetime — so passing it meant nothing
    /// was ever cached and the stale fallback in spec §4.3 could never trigger. The
    /// repository applies its own write budget.
    /// </summary>
    private async Task TryCacheUpsertAsync(
        Vin vin,
        SourceSystem system,
        IReadOnlyList<VehicleDocument> documents)
    {
        try
        {
            await cache.UpsertAsync(vin, system, documents, CacheTtl, CancellationToken.None);
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
