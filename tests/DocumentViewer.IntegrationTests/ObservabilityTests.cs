using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using DocumentViewer.Domain;
using DocumentViewer.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Shouldly;

namespace DocumentViewer.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ObservabilityTests(PostgresContainerFixture postgres)
{
    private const string TestVin = "1HGCM82633A004352";
    private const string CancelVin = "1FTFW1ET7EFA00003";

    [Fact]
    public async Task Echoes_inbound_X_Correlation_Id_on_the_response()
    {
        await using var harness = CreateHarness(postgres.ConnectionString);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/vehicles/{TestVin}/documents");
        request.Headers.Add("X-Correlation-Id", "keyloop-corr-2026-001");

        using var response = await harness.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Correlation-Id", out var values).ShouldBeTrue();
        values.ShouldHaveSingleItem().ShouldBe("keyloop-corr-2026-001");
    }

    [Fact]
    public async Task The_harness_itself_delivers_a_document_from_each_stubbed_source()
    {
        // Guards the harness, not the product. Both upstream stubs previously used field
        // names the normalisers do not read ("name" for Sales, "title" for Service), so
        // every document was skipped and every test in this class ran against a
        // zero-document response — and still passed, because none of them counted.
        await using var harness = CreateHarness(postgres.ConnectionString);

        using var response = await harness.Client.GetAsync($"/api/v1/vehicles/{TestVin}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("documents").GetArrayLength().ShouldBe(
            2,
            "one document from each stubbed source; a skipped record means a stub field name is wrong");
        body.RootElement.GetProperty("partial").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Generates_a_correlation_id_when_none_supplied()
    {
        await using var harness = CreateHarness(postgres.ConnectionString);

        using var response = await harness.Client.GetAsync($"/api/v1/vehicles/{TestVin}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Correlation-Id", out var values).ShouldBeTrue();
        var correlationId = values.ShouldHaveSingleItem();
        correlationId.ShouldNotBeNullOrWhiteSpace();
        correlationId.Length.ShouldBeGreaterThanOrEqualTo(16);
    }

    [Fact]
    public async Task Propagates_correlation_id_to_both_upstream_requests()
    {
        await using var harness = CreateHarness(postgres.ConnectionString);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/vehicles/{TestVin}/documents");
        request.Headers.Add("X-Correlation-Id", "corr-prop-7788");

        using var response = await harness.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        harness.UpstreamStub.CapturedRequests.Count.ShouldBe(2);
        foreach (var upstreamRequest in harness.UpstreamStub.CapturedRequests)
        {
            upstreamRequest.CorrelationId.ShouldBe("corr-prop-7788");
        }
    }

    [Fact]
    public async Task Emits_two_overlapping_upstream_spans_for_one_request()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var traceparent = $"00-{traceId}-{parentSpanId}-01";

        var captured = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name is "DocumentViewer" or "Microsoft.AspNetCore",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId)
                {
                    captured.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        await using var harness = CreateHarness(
            postgres.ConnectionString,
            salesDelay: TimeSpan.FromMilliseconds(200),
            serviceDelay: TimeSpan.FromMilliseconds(200));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/vehicles/{TestVin}/documents");
        request.Headers.Add("traceparent", traceparent);

        using var response = await harness.Client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var upstreamSpans = captured
            .Where(static a => a.Source.Name == "DocumentViewer" && a.GetTagItem("upstream.system") is not null)
            .ToList();

        upstreamSpans.Count.ShouldBe(2);

        var salesSpan = upstreamSpans.Single(static a => Equals(a.GetTagItem("upstream.system"), "SalesSystem"));
        var serviceSpan = upstreamSpans.Single(static a => Equals(a.GetTagItem("upstream.system"), "ServiceSystem"));

        salesSpan.ParentSpanId.ShouldNotBe(default);
        salesSpan.ParentSpanId.ShouldBe(serviceSpan.ParentSpanId);

        (salesSpan.StartTimeUtc < serviceSpan.StartTimeUtc + serviceSpan.Duration &&
         serviceSpan.StartTimeUtc < salesSpan.StartTimeUtc + salesSpan.Duration)
            .ShouldBeTrue(
                $"Expected overlapping spans, got Sales [{salesSpan.StartTimeUtc:O} + {salesSpan.Duration.TotalMilliseconds}ms] " +
                $"and Service [{serviceSpan.StartTimeUtc:O} + {serviceSpan.Duration.TotalMilliseconds}ms]");
    }

    [Fact]
    public async Task No_log_line_contains_the_raw_vin()
    {
        Vin.TryParse(TestVin, out var parsedVin, out _).ShouldBeTrue();

        await using var harness = CreateHarness(
            postgres.ConnectionString,
            serviceStatus: HttpStatusCode.ServiceUnavailable);

        using var response = await harness.Client.GetAsync($"/api/v1/vehicles/{TestVin}/documents");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var lines = harness.LogSink.RenderedLines;
        lines.ShouldNotBeEmpty();

        foreach (var line in lines)
        {
            line.ShouldNotContain(TestVin);
        }

        lines.ShouldContain(l => l.Contains(parsedVin.Hash12, StringComparison.Ordinal));
        lines.ShouldContain(l => l.Contains("CorrelationId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_live_returns_200_when_both_upstreams_are_down()
    {
        await using var harness = CreateHarness(
            BrokenConnectionString,
            salesStatus: HttpStatusCode.ServiceUnavailable,
            serviceStatus: HttpStatusCode.ServiceUnavailable);

        using var response = await harness.Client.GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ready_returns_200_when_both_upstreams_are_down()
    {
        await using var harness = CreateHarness(
            postgres.ConnectionString,
            salesStatus: HttpStatusCode.ServiceUnavailable,
            serviceStatus: HttpStatusCode.ServiceUnavailable);

        using var response = await harness.Client.GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ready_returns_503_when_the_database_is_unreachable()
    {
        await using var harness = CreateHarness(BrokenConnectionString);

        using var response = await harness.Client.GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Client_cancellation_is_not_logged_as_error_and_writes_no_audit_row()
    {
        var correlationId = $"cancel-{Guid.NewGuid():N}";
        await using var harness = CreateHarness(
            postgres.ConnectionString,
            salesDelay: TimeSpan.FromMilliseconds(600),
            serviceDelay: TimeSpan.FromMilliseconds(600));

        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/vehicles/{CancelVin}/documents");
        request.Headers.Add("X-Correlation-Id", correlationId);

        var call = harness.Client.SendAsync(request, cts.Token);
        cts.CancelAfter(50);

        await Should.ThrowAsync<TaskCanceledException>(() => call);
        await Task.Delay(200);

        harness.LogSink.Events.ShouldNotContain(e => e.Level >= LogEventLevel.Error);

        await using var db = await postgres.DbContextFactory.CreateDbContextAsync();
        var auditCount = await db.SearchAudits.CountAsync(a => a.Vin == CancelVin && a.CorrelationId == correlationId);
        auditCount.ShouldBe(0);
    }

    private const string BrokenConnectionString =
        "Host=127.0.0.1;Port=59999;Database=unreachable_db;Username=postgres;Password=postgres;Timeout=1;Command Timeout=1;Pooling=false";

    private static ObservabilityHarness CreateHarness(
        string connectionString,
        HttpStatusCode salesStatus = HttpStatusCode.OK,
        HttpStatusCode serviceStatus = HttpStatusCode.OK,
        TimeSpan? salesDelay = null,
        TimeSpan? serviceDelay = null)
    {
        var logSink = new InMemoryJsonLogSink();
        var upstreamStub = new StubUpstreamHandler(
            salesStatus,
            serviceStatus,
            salesDelay ?? TimeSpan.Zero,
            serviceDelay ?? TimeSpan.Zero);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = connectionString,
                    ["Observability:UseConsoleExporter"] = "false",
                    ["Upstreams:CircuitBreakerMinimumThroughput"] = "100"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILogEventSink>(logSink);
                services.AddSingleton<IHttpMessageHandlerBuilderFilter>(
                    new ReplacePrimaryHandlerFilter(upstreamStub));
            });
        });

        return new ObservabilityHarness(factory, factory.CreateClient(), logSink, upstreamStub);
    }

    private sealed class ObservabilityHarness(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        InMemoryJsonLogSink logSink,
        StubUpstreamHandler upstreamStub) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public InMemoryJsonLogSink LogSink => logSink;

        public StubUpstreamHandler UpstreamStub => upstreamStub;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await factory.DisposeAsync();
        }
    }

    private sealed class ReplacePrimaryHandlerFilter(HttpMessageHandler primaryHandler) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) =>
            builder =>
            {
                next(builder);
                if (builder.Name is "SalesSystem" or "ServiceSystem")
                {
                    builder.PrimaryHandler = new ForwardingHandler(primaryHandler);
                }
            };
    }

    private sealed class ForwardingHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(inner, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);
    }

    private sealed record CapturedUpstreamRequest(Uri Uri, string? CorrelationId);

    private sealed class StubUpstreamHandler(
        HttpStatusCode salesStatus,
        HttpStatusCode serviceStatus,
        TimeSpan salesDelay,
        TimeSpan serviceDelay) : HttpMessageHandler
    {
        public ConcurrentQueue<CapturedUpstreamRequest> CapturedRequests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var correlationId = request.Headers.TryGetValues("X-Correlation-Id", out var values)
                ? values.FirstOrDefault()
                : null;
            CapturedRequests.Enqueue(new CapturedUpstreamRequest(request.RequestUri!, correlationId));

            var isSales = request.RequestUri!.AbsolutePath.StartsWith("/sales/", StringComparison.OrdinalIgnoreCase);
            var delay = isSales ? salesDelay : serviceDelay;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var status = isSales ? salesStatus : serviceStatus;
            if (status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(status);
            }

            var json = isSales
                ? """
                  {
                    "documents": [
                      {
                        "documentId": "PO-88213",
                        "docType": "PURCHASE_ORDER",
                        "description": "Vehicle purchase order",
                        "createdDate": "2026-10-15",
                        "fileSizeKb": 145,
                        "url": "https://sales.mock/documents/PO-88213"
                      }
                    ]
                  }
                  """
                : """
                  {
                    "data": {
                      "items": [
                        {
                          "id": 88213,
                          "category": "JobCard",
                          "name": "Annual service job card",
                          "timestamp": 1760500000000,
                          "bytes": 148213,
                          "links": { "self": "https://service.mock/docs/88213" }
                        }
                      ]
                    },
                    "meta": { "source": "ServiceV2" }
                  }
                  """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class InMemoryJsonLogSink : ILogEventSink
    {
        private readonly RenderedCompactJsonFormatter _formatter = new();
        private readonly ConcurrentQueue<LogEvent> _events = new();
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<LogEvent> Events => _events;

        public IReadOnlyCollection<string> RenderedLines => _lines;

        public void Emit(LogEvent logEvent)
        {
            _events.Enqueue(logEvent);
            using var writer = new StringWriter();
            _formatter.Format(logEvent, writer);
            _lines.Enqueue(writer.ToString());
        }
    }
}
