using System.Diagnostics;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Application.Observability;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace DocumentViewer.UnitTests.Aggregation;

public sealed class DocumentAggregationServiceTests
{
    private static readonly Vin TestVin = ParseVin("1HGCM82633A004352");
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Merges_documents_from_all_sources()
    {
        var sales = Source(SourceSystem.SalesSystem, Docs(SourceSystem.SalesSystem, 2));
        var service = Source(SourceSystem.ServiceSystem, Docs(SourceSystem.ServiceSystem, 3));

        var result = await Service(sales, service).GetDocumentsAsync(TestVin, CancellationToken.None);

        result.Documents.Count.ShouldBe(5);
        result.Documents.Count(d => d.SourceSystem == SourceSystem.SalesSystem).ShouldBe(2);
        result.Documents.Count(d => d.SourceSystem == SourceSystem.ServiceSystem).ShouldBe(3);
        result.RetrievedAt.ShouldBe(FixedNow);
    }

    [Fact]
    public async Task Orders_documents_by_issuedAt_descending_with_nulls_last()
    {
        var older = new VehicleDocument("sales:2", SourceSystem.SalesSystem, DocumentType.PurchaseOrder, "Older", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), 100, null);
        var newerB = new VehicleDocument("service:9", SourceSystem.ServiceSystem, DocumentType.JobCard, "Newer B", new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), 100, null);
        var newerA = new VehicleDocument("sales:1", SourceSystem.SalesSystem, DocumentType.FinanceAgreement, "Newer A", new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), 100, null);
        var undated = new VehicleDocument("service:1", SourceSystem.ServiceSystem, DocumentType.InspectionReport, "Undated", null, 100, null);

        var sales = Source(SourceSystem.SalesSystem, [older, newerA]);
        var service = Source(SourceSystem.ServiceSystem, [undated, newerB]);

        var result = await Service(sales, service).GetDocumentsAsync(TestVin, CancellationToken.None);

        result.Documents.Select(d => d.Id).ShouldBe(["sales:1", "service:9", "sales:2", "service:1"]);
    }

    [Fact]
    public async Task Reports_ok_for_each_successful_source_and_partial_is_false()
    {
        var sales = Source(SourceSystem.SalesSystem, Docs(SourceSystem.SalesSystem, 2));
        var service = Source(SourceSystem.ServiceSystem, Docs(SourceSystem.ServiceSystem, 1));

        var result = await Service(sales, service).GetDocumentsAsync(TestVin, CancellationToken.None);

        result.Partial.ShouldBeFalse();
        result.AllSourcesFailed.ShouldBeFalse();
        result.Sources.Count.ShouldBe(2);
        result.Sources.ShouldAllBe(s => s.Status == SourceStatus.Ok && s.Error == null);
    }

    [Fact]
    public async Task One_source_failing_still_returns_the_other_sources_documents()
    {
        var ok = Source(SourceSystem.SalesSystem, Docs(SourceSystem.SalesSystem, 3));
        var broken = FailingSource(SourceSystem.ServiceSystem, UpstreamFailureReason.HttpError, 503);

        var result = await Service(ok, broken).GetDocumentsAsync(TestVin, CancellationToken.None);

        result.Documents.Count.ShouldBe(3);
        result.Partial.ShouldBeTrue();
        result.AllSourcesFailed.ShouldBeFalse();
        result.Sources.Single(s => s.System == SourceSystem.ServiceSystem).Status.ShouldBe(SourceStatus.Failed);
    }

    [Fact]
    public async Task A_throwing_source_does_not_lose_the_sibling_source_result()
    {
        var ok = Source(SourceSystem.SalesSystem, Docs(SourceSystem.SalesSystem, 2));
        var throwing = ThrowingSource(SourceSystem.ServiceSystem, new InvalidOperationException("boom"));

        var result = await Service(ok, throwing).GetDocumentsAsync(TestVin, CancellationToken.None);

        result.Documents.Count.ShouldBe(2);
        result.Partial.ShouldBeTrue();
        var failedReport = result.Sources.Single(s => s.System == SourceSystem.ServiceSystem);
        failedReport.Status.ShouldBe(SourceStatus.Failed);
        failedReport.Error!.Reason.ShouldBe(UpstreamFailureReason.Unknown);
    }

    [Fact]
    public async Task Calls_sources_in_parallel_not_sequentially()
    {
        var a = DelayedSource(SourceSystem.SalesSystem, TimeSpan.FromMilliseconds(400), Docs(SourceSystem.SalesSystem, 1));
        var b = DelayedSource(SourceSystem.ServiceSystem, TimeSpan.FromMilliseconds(400), Docs(SourceSystem.ServiceSystem, 1));

        var sw = Stopwatch.StartNew();
        await Service(a, b).GetDocumentsAsync(TestVin, CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(700);
    }

    [Fact]
    public async Task All_sources_failing_sets_AllSourcesFailed()
    {
        var a = FailingSource(SourceSystem.SalesSystem, UpstreamFailureReason.Timeout, null);
        var b = FailingSource(SourceSystem.ServiceSystem, UpstreamFailureReason.HttpError, 503);

        var result = await Service(a, b).GetDocumentsAsync(TestVin, CancellationToken.None);

        result.AllSourcesFailed.ShouldBeTrue();
        result.Partial.ShouldBeTrue();
        result.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_source_with_cached_entry_is_reported_Stale_and_its_documents_included()
    {
        var cache = Substitute.For<IDocumentCacheRepository>();
        var cachedDocs = Docs(SourceSystem.ServiceSystem, 2);
        cache.GetAsync(TestVin, SourceSystem.ServiceSystem, Arg.Any<CancellationToken>())
            .Returns(new CachedSourceDocuments(cachedDocs, FixedNow.AddMinutes(-5)));

        var liveSales = Source(SourceSystem.SalesSystem, Docs(SourceSystem.SalesSystem, 1));
        var brokenService = FailingSource(SourceSystem.ServiceSystem, UpstreamFailureReason.Timeout, null);

        var result = await ServiceWithCache(cache, liveSales, brokenService).GetDocumentsAsync(TestVin, CancellationToken.None);

        result.Documents.Count.ShouldBe(3);
        result.Partial.ShouldBeTrue();
        result.AllSourcesFailed.ShouldBeFalse();
        var serviceReport = result.Sources.Single(s => s.System == SourceSystem.ServiceSystem);
        serviceReport.Status.ShouldBe(SourceStatus.Stale);
        serviceReport.DocumentCount.ShouldBe(2);
    }

    [Fact]
    public async Task Successful_source_results_are_written_to_cache()
    {
        var cache = Substitute.For<IDocumentCacheRepository>();
        var salesDocs = Docs(SourceSystem.SalesSystem, 2);
        var sales = Source(SourceSystem.SalesSystem, salesDocs);

        await ServiceWithCache(cache, sales).GetDocumentsAsync(TestVin, CancellationToken.None);

        await cache.Received(1).UpsertAsync(
            TestVin,
            SourceSystem.SalesSystem,
            Arg.Is<IReadOnlyList<VehicleDocument>>(d => d.Count == 2),
            TimeSpan.FromMinutes(15),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_source_results_are_never_written_to_cache()
    {
        var cache = Substitute.For<IDocumentCacheRepository>();
        var broken = FailingSource(SourceSystem.SalesSystem, UpstreamFailureReason.HttpError, 500);

        await ServiceWithCache(cache, broken).GetDocumentsAsync(TestVin, CancellationToken.None);

        await cache.DidNotReceiveWithAnyArgs().UpsertAsync(default, default, default!, default, default);
    }

    [Fact]
    public async Task Empty_results_from_all_sources_yield_empty_list_and_partial_false()
    {
        var sales = Source(SourceSystem.SalesSystem, []);
        var service = Source(SourceSystem.ServiceSystem, []);

        var result = await Service(sales, service).GetDocumentsAsync(TestVin, CancellationToken.None);

        result.Documents.ShouldBeEmpty();
        result.Partial.ShouldBeFalse();
        result.AllSourcesFailed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_hung_database_cannot_discard_a_healthy_source_when_the_request_budget_expires()
    {
        // The cache read happens AFTER the fan-out, so it runs at ~5s whenever a source
        // hits its 5s total timeout — about 1s before the 6s whole-request budget. If a
        // hung database then let an OperationCanceledException out of the cache, it
        // propagated through GetDocumentsAsync and threw away the healthy source's
        // already-completed documents: a 503 with nothing in it, which is the exact
        // failure UpstreamResult<T> exists to prevent (spec §4.2).
        var healthy = Source(SourceSystem.SalesSystem, Docs(SourceSystem.SalesSystem, 2));
        var broken = FailingSource(SourceSystem.ServiceSystem, UpstreamFailureReason.HttpError, 503);
        using var requestCts = new CancellationTokenSource();
        requestCts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var result = await ServiceWithCache(new HangingCache(), healthy, broken)
            .GetDocumentsAsync(TestVin, requestCts.Token);

        result.Documents.Count.ShouldBe(2, "the healthy source's documents must survive");
        result.Partial.ShouldBeTrue();
        result.AllSourcesFailed.ShouldBeFalse();
        result.Sources.Single(r => r.System == SourceSystem.ServiceSystem).Status
            .ShouldBe(SourceStatus.Failed);
    }

    [Fact]
    public async Task A_cache_lookup_that_exceeds_its_budget_is_recorded_as_a_timeout_not_a_hit()
    {
        // The abandoned lookup must not record anything later: §7.2 says this metric
        // answers "is the cache earning its complexity", and a hit recorded after the
        // response went out without it is the opposite of that answer.
        var metrics = new RecordingMetrics();
        var broken = FailingSource(SourceSystem.ServiceSystem, UpstreamFailureReason.HttpError, 503);
        var cache = new SlowReadCache(TimeSpan.FromSeconds(2), Docs(SourceSystem.ServiceSystem, 1));

        var result = await ServiceWithCache(cache, metrics, broken)
            .GetDocumentsAsync(TestVin, CancellationToken.None);

        result.Sources.Single().Status.ShouldBe(SourceStatus.Failed);
        metrics.CacheLookups.ShouldBe(["timeout"]);

        await cache.Finished;
        await Task.Delay(100);
        metrics.CacheLookups.ShouldBe(["timeout"], "the abandoned lookup must record nothing");
    }

    private sealed class HangingCache : IDocumentCacheRepository
    {
        public async Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }

        public Task UpsertAsync(
            Vin vin,
            SourceSystem system,
            IReadOnlyList<VehicleDocument> documents,
            TimeSpan ttl,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SlowReadCache(TimeSpan readDuration, IReadOnlyList<VehicleDocument> documents)
        : IDocumentCacheRepository
    {
        private readonly TaskCompletionSource finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Finished => finished.Task;

        public async Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct)
        {
            try
            {
                await Task.Delay(readDuration, ct);
                return new CachedSourceDocuments(documents, DateTimeOffset.UtcNow);
            }
            finally
            {
                finished.TrySetResult();
            }
        }

        public Task UpsertAsync(
            Vin vin,
            SourceSystem system,
            IReadOnlyList<VehicleDocument> documents,
            TimeSpan ttl,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingMetrics : IAggregationMetrics
    {
        private readonly List<string> cacheLookups = [];

        public IReadOnlyList<string> CacheLookups
        {
            get { lock (cacheLookups) { return cacheLookups.ToList(); } }
        }

        public void RecordAggregation(string outcome, TimeSpan duration) { }

        public void RecordUpstream(SourceSystem system, SourceStatus status, TimeSpan duration) { }

        public void RecordPartial() { }

        public void RecordCacheLookup(string result)
        {
            lock (cacheLookups) { cacheLookups.Add(result); }
        }
    }

    [Fact]
    public async Task Cache_writes_outlive_the_request_and_never_hold_the_response()
    {
        // Found against live Supabase: cache writes ran on the request's cancellation
        // token, which is cancelled the moment the response is sent. A cross-region
        // write never finished, so nothing was ever cached and the stale fallback in
        // spec §4.3 could not work. A slow write must therefore (a) not hold the read
        // and (b) still complete after the request token is cancelled.
        var cache = new SlowWriteCache(TimeSpan.FromSeconds(2));
        var sales = Source(SourceSystem.SalesSystem, Docs(SourceSystem.SalesSystem, 2));
        using var requestCts = new CancellationTokenSource();

        var sw = Stopwatch.StartNew();
        await ServiceWithCache(cache, sales).GetDocumentsAsync(TestVin, requestCts.Token);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(
            TimeSpan.FromMilliseconds(1800),
            "a slow cache write must not be awaited on the response path");

        await requestCts.CancelAsync();

        await cache.Finished;
        cache.CompletedWrites.ShouldBe(1);
        cache.ObservedCancellation.ShouldBeFalse(
            "cache writes must not observe the request's cancellation token");
    }

    private sealed class SlowWriteCache(TimeSpan writeDuration) : IDocumentCacheRepository
    {
        private readonly TaskCompletionSource finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int completedWrites;

        public Task Finished => finished.Task;

        public int CompletedWrites => Volatile.Read(ref completedWrites);

        public bool ObservedCancellation { get; private set; }

        public Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct) =>
            Task.FromResult<CachedSourceDocuments?>(null);

        public async Task UpsertAsync(
            Vin vin,
            SourceSystem system,
            IReadOnlyList<VehicleDocument> documents,
            TimeSpan ttl,
            CancellationToken ct)
        {
            try
            {
                await Task.Delay(writeDuration, ct);
                Interlocked.Increment(ref completedWrites);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
            }
            finally
            {
                finished.TrySetResult();
            }
        }
    }

    private static DocumentAggregationService Service(params IDocumentSource[] sources) =>
        ServiceWithCache(new NoOpCache(), sources);

    private static DocumentAggregationService ServiceWithCache(IDocumentCacheRepository cache, params IDocumentSource[] sources) =>
        ServiceWithCache(cache, new NoOpMetrics(), sources);

    private static DocumentAggregationService ServiceWithCache(
        IDocumentCacheRepository cache,
        IAggregationMetrics metrics,
        params IDocumentSource[] sources) =>
        new(sources, cache, metrics, new FixedTimeProvider(FixedNow), NullLogger<DocumentAggregationService>.Instance);

    private static IDocumentSource Source(SourceSystem system, IReadOnlyList<VehicleDocument> docs)
    {
        var src = Substitute.For<IDocumentSource>();
        src.System.Returns(system);
        src.FetchAsync(Arg.Any<Vin>(), Arg.Any<CancellationToken>())
            .Returns(new UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok(docs, TimeSpan.FromMilliseconds(25)));
        return src;
    }

    private static IDocumentSource FailingSource(SourceSystem system, UpstreamFailureReason reason, int? statusCode)
    {
        var src = Substitute.For<IDocumentSource>();
        src.System.Returns(system);
        src.FetchAsync(Arg.Any<Vin>(), Arg.Any<CancellationToken>())
            .Returns(new UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed(
                new UpstreamFailure(reason, "upstream failed", statusCode),
                TimeSpan.FromMilliseconds(50)));
        return src;
    }

    private static IDocumentSource ThrowingSource(SourceSystem system, Exception exception) =>
        new DelegateDocumentSource(system, (_, _) => Task.FromException<UpstreamResult<IReadOnlyList<VehicleDocument>>>(exception));

    private static IDocumentSource DelayedSource(SourceSystem system, TimeSpan delay, IReadOnlyList<VehicleDocument> docs) =>
        new DelegateDocumentSource(system, async (_, ct) =>
        {
            await Task.Delay(delay, ct);
            return new UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok(docs, delay);
        });

    private static IReadOnlyList<VehicleDocument> Docs(SourceSystem system, int count)
    {
        var prefix = system == SourceSystem.SalesSystem ? "sales" : "service";
        return Enumerable.Range(1, count)
            .Select(i => new VehicleDocument(
                $"{prefix}:{i}",
                system,
                DocumentType.PurchaseOrder,
                $"Doc {i}",
                FixedNow.AddDays(-i),
                1024L * i,
                $"https://{prefix}.mock/docs/{i}"))
            .ToList();
    }

    private static Vin ParseVin(string raw)
    {
        Vin.TryParse(raw, out var vin, out _).ShouldBeTrue();
        return vin;
    }

    private sealed class DelegateDocumentSource(
        SourceSystem system,
        Func<Vin, CancellationToken, Task<UpstreamResult<IReadOnlyList<VehicleDocument>>>> fetch) : IDocumentSource
    {
        public SourceSystem System => system;

        public Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct) =>
            fetch(vin, ct);
    }

    private sealed class NoOpCache : IDocumentCacheRepository
    {
        public Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct) =>
            Task.FromResult<CachedSourceDocuments?>(null);

        public Task UpsertAsync(Vin vin, SourceSystem system, IReadOnlyList<VehicleDocument> documents, TimeSpan ttl, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class NoOpMetrics : IAggregationMetrics
    {
        public void RecordAggregation(string outcome, TimeSpan duration) { }
        public void RecordUpstream(SourceSystem system, SourceStatus status, TimeSpan duration) { }
        public void RecordPartial() { }
        public void RecordCacheLookup(string result) { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
