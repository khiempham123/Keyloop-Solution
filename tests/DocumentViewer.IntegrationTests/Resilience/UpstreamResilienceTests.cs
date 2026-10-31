using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentViewer.Api.Contracts;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Domain;
using DocumentViewer.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace DocumentViewer.IntegrationTests.Resilience;

/// <summary>
/// Proves the reliability rules of spec §8 against upstreams that can be scripted to
/// misbehave. These are the tests the scenario exists for: requirement #2 says the
/// backend must fan out in parallel, and nothing short of measuring it proves that.
/// </summary>
public sealed class UpstreamResilienceTests
{
    private const string TestVin = "1HGCM82633A004352";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Parallel_fan_out_takes_roughly_one_upstream_latency_not_two()
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        await using var factory = CreateFactory(upstreams);
        using var client = factory.CreateClient();
        await WarmUpAsync(client, upstreams);

        var oneSecond = TimeSpan.FromSeconds(1);
        upstreams.StubHealthy(
            upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin),
            WireMockUpstreamFixture.SalesPayload(2), oneSecond);
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(2), oneSecond);

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync(Endpoint);
        sw.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Read(response);
        body.Documents.Count.ShouldBe(4);
        sw.Elapsed.ShouldBeLessThan(
            TimeSpan.FromMilliseconds(1600),
            "two sequential one-second calls would take at least two seconds");
    }

    [Fact]
    public async Task One_upstream_timing_out_returns_200_partial_within_the_total_budget()
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        upstreams.StubHealthy(
            upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin),
            WireMockUpstreamFixture.SalesPayload(3));
        upstreams.StubStatus(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            200, TimeSpan.FromSeconds(30));

        await using var factory = CreateFactory(upstreams);
        using var client = factory.CreateClient();

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync(Endpoint);
        sw.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Read(response);
        body.Partial.ShouldBeTrue();
        body.Documents.Count.ShouldBe(3);
        Source(body, SourceSystem.ServiceSystem).Status.ShouldBe(SourceStatus.Failed);
        Source(body, SourceSystem.ServiceSystem).Error!.Reason.ShouldBe(UpstreamFailureReason.Timeout);
        sw.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(7),
            "the whole-request budget is 6s; a slow source must not exceed it");
    }

    [Fact]
    public async Task Both_upstreams_timing_out_returns_503()
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        var forever = TimeSpan.FromSeconds(30);
        upstreams.StubStatus(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin), 200, forever);
        upstreams.StubStatus(upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin), 200, forever);

        await using var factory = CreateFactory(upstreams);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Transient_500_is_retried_and_succeeds_on_the_second_attempt()
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        upstreams.StubFailingThenHealthy(
            upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin),
            failures: 1, failureStatusCode: 500,
            payload: WireMockUpstreamFixture.SalesPayload(2));
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(1));

        await using var factory = CreateFactory(upstreams);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Read(response);
        body.Partial.ShouldBeFalse();
        Source(body, SourceSystem.SalesSystem).Status.ShouldBe(SourceStatus.Ok);
        upstreams.RequestCount(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin))
            .ShouldBe(2, "one failed attempt plus one retry");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    public async Task Client_error_responses_are_never_retried(int statusCode)
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        upstreams.StubStatus(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin), statusCode);
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(1));

        await using var factory = CreateFactory(upstreams);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Read(response);
        body.Partial.ShouldBeTrue();
        Source(body, SourceSystem.SalesSystem).Error!.Reason.ShouldBe(UpstreamFailureReason.HttpError);
        upstreams.RequestCount(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin))
            .ShouldBe(1, $"{statusCode} is the upstream answering correctly; retrying only multiplies load");
    }

    [Fact]
    public async Task Repeated_failures_open_the_circuit_and_subsequent_calls_fail_fast()
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        upstreams.StubStatus(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin), 503);
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(1));

        await using var factory = CreateFactory(upstreams, breakerMinimumThroughput: 2);
        using var client = factory.CreateClient();
        await WarmUpAsync(client, upstreams);
        upstreams.StubStatus(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin), 503);
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(1));

        UpstreamFailureReason? lastReason = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            // Read asserts 200, so a both-sources-failed response fails loudly here
            // rather than being swallowed and reported later as a misleading null.
            lastReason = Source(await Read(await client.GetAsync(Endpoint)), SourceSystem.SalesSystem)
                .Error!.Reason;
            if (lastReason == UpstreamFailureReason.CircuitOpen)
            {
                break;
            }
        }

        lastReason.ShouldBe(UpstreamFailureReason.CircuitOpen);

        var requestsBefore = upstreams.RequestCount(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin));
        await client.GetAsync(Endpoint);
        upstreams.RequestCount(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin))
            .ShouldBe(requestsBefore, "an open circuit must not reach the upstream at all");
    }

    [Fact]
    public async Task Open_circuit_on_one_source_does_not_affect_the_other_source()
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        upstreams.StubStatus(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin), 503);
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(3));

        await using var factory = CreateFactory(upstreams, breakerMinimumThroughput: 2);
        using var client = factory.CreateClient();
        await WarmUpAsync(client, upstreams);
        upstreams.StubStatus(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin), 503);
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(3));

        DocumentsResponse body = null!;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            body = await Read(await client.GetAsync(Endpoint));
            if (Source(body, SourceSystem.SalesSystem).Error!.Reason == UpstreamFailureReason.CircuitOpen)
            {
                break;
            }
        }

        body.ShouldNotBeNull("no successful response was observed while the breaker opened");

        Source(body, SourceSystem.SalesSystem).Error!.Reason.ShouldBe(UpstreamFailureReason.CircuitOpen);
        Source(body, SourceSystem.ServiceSystem).Status.ShouldBe(SourceStatus.Ok);
        body.Documents.Count.ShouldBe(3, "the healthy source keeps serving while the other is broken");
        body.Documents.ShouldAllBe(d => d.SourceSystem == SourceSystem.ServiceSystem);
    }

    [Fact]
    public async Task Malformed_upstream_payload_reports_InvalidPayload_not_500()
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        upstreams.StubGarbage(upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin));
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(2));

        await using var factory = CreateFactory(upstreams);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Read(response);
        body.Partial.ShouldBeTrue();
        Source(body, SourceSystem.SalesSystem).Error!.Reason.ShouldBe(UpstreamFailureReason.InvalidPayload);
        body.Documents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Stale_cache_is_served_when_an_upstream_fails_after_a_successful_fetch()
    {
        using var upstreams = WireMockUpstreamFixture.Start();
        upstreams.StubHealthy(
            upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin),
            WireMockUpstreamFixture.SalesPayload(2));
        upstreams.StubStatus(upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin), 503);

        var cache = new InMemoryCache();
        cache.Seed(
            TestVin,
            SourceSystem.ServiceSystem,
            [
                new VehicleDocument(
                    "service:91001",
                    SourceSystem.ServiceSystem,
                    DocumentType.JobCard,
                    "Job card #91001",
                    DateTimeOffset.UtcNow.AddDays(-3),
                    148213,
                    "https://service.mock/docs/91001")
            ]);

        await using var factory = CreateFactory(upstreams, cache: cache);
        using var client = factory.CreateClient();

        var body = await Read(await client.GetAsync(Endpoint));

        body.Partial.ShouldBeTrue();
        Source(body, SourceSystem.ServiceSystem).Status.ShouldBe(SourceStatus.Stale);
        body.Documents.Count.ShouldBe(3, "two live from Sales plus one stale from Service");
    }

    /// <summary>
    /// Sends one throwaway request through the host so the measurement that follows is
    /// not paying for host build, JIT and the first socket. Without this,
    /// <c>Parallel_fan_out…</c> failed every time it was run on its own — it only passed
    /// because earlier tests had warmed the process, which is a test that measures the
    /// order it was run in rather than the product.
    /// </summary>
    private static async Task WarmUpAsync(HttpClient client, WireMockUpstreamFixture upstreams)
    {
        upstreams.StubHealthy(
            upstreams.Sales, WireMockUpstreamFixture.SalesPath(TestVin),
            WireMockUpstreamFixture.SalesPayload(1));
        upstreams.StubHealthy(
            upstreams.Service, WireMockUpstreamFixture.ServicePath(TestVin),
            WireMockUpstreamFixture.ServicePayload(1));

        (await client.GetAsync(Endpoint)).StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "the warm-up request itself must succeed, or the test environment is the problem");

        upstreams.Sales.Reset();
        upstreams.Service.Reset();
    }

    private static string Endpoint => $"/api/v1/vehicles/{TestVin}/documents";

    private static SourceItemResponse Source(DocumentsResponse body, SourceSystem system) =>
        body.Sources.Single(s => s.System == system);

    private static async Task<DocumentsResponse> Read(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"body was: {raw}");
        var body = JsonSerializer.Deserialize<DocumentsResponse>(raw, Json);
        body.ShouldNotBeNull();
        return body;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        WireMockUpstreamFixture upstreams,
        IDocumentCacheRepository? cache = null,
        int? breakerMinimumThroughput = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Upstreams:SalesSystem:BaseAddress"] = upstreams.SalesBaseAddress,
                    ["Upstreams:ServiceSystem:BaseAddress"] = upstreams.ServiceBaseAddress,
                    // No database: these tests are about upstream behaviour, and the
                    // cache is supplied directly where a test needs one.
                    ["ConnectionStrings:Default"] = string.Empty
                };

                if (breakerMinimumThroughput is not null)
                {
                    settings["Upstreams:CircuitBreakerMinimumThroughput"] =
                        breakerMinimumThroughput.Value.ToString();
                }

                config.AddInMemoryCollection(settings);
            });

            if (cache is not null)
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDocumentCacheRepository>();
                    services.AddSingleton(cache);
                });
            }
        });

    private sealed class InMemoryCache : IDocumentCacheRepository
    {
        private readonly Dictionary<string, CachedSourceDocuments> entries = [];

        public void Seed(string vin, SourceSystem system, IReadOnlyList<VehicleDocument> documents) =>
            entries[Key(vin, system)] = new CachedSourceDocuments(documents, DateTimeOffset.UtcNow.AddMinutes(-1));

        public Task<CachedSourceDocuments?> GetAsync(Vin vin, SourceSystem system, CancellationToken ct) =>
            Task.FromResult(entries.GetValueOrDefault(Key(vin.Value, system)));

        public Task UpsertAsync(
            Vin vin,
            SourceSystem system,
            IReadOnlyList<VehicleDocument> documents,
            TimeSpan ttl,
            CancellationToken ct)
        {
            entries[Key(vin.Value, system)] = new CachedSourceDocuments(documents, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }

        private static string Key(string vin, SourceSystem system) => $"{vin}:{system}";
    }
}
