using System.Net;
using System.Text;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using DocumentViewer.Infrastructure;
using DocumentViewer.Infrastructure.Sources.Sales;
using DocumentViewer.Infrastructure.Sources.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Shouldly;

namespace DocumentViewer.UnitTests.Sources;

public sealed class DocumentSourceClientTests
{
    private static readonly Vin TestVin = ParseVin("1HGCM82633A004352");

    [Fact]
    public async Task SalesSystemClient_returns_Ok_with_normalized_documents_on_200()
    {
        const string json = """
            {
              "documents": [
                {
                  "documentId": "PO-88213",
                  "docType": "PURCHASE_ORDER",
                  "description": "Purchase order PO-88213",
                  "createdDate": "2026-03-04",
                  "fileSizeKb": 145,
                  "url": "https://sales.mock/documents/PO-88213"
                }
              ]
            }
            """;

        var client = CreateSalesClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        var result = await client.FetchAsync(TestVin, CancellationToken.None);

        var ok = result.ShouldBeOfType<UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok>();
        ok.Value.ShouldHaveSingleItem().Id.ShouldBe("sales:PO-88213");
        ok.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task ServiceSystemClient_returns_Ok_with_normalized_documents_on_200()
    {
        const string json = """
            {
              "data": {
                "items": [
                  {
                    "id": 88213,
                    "category": "job-card",
                    "name": "Job card #88213",
                    "timestamp": 1772582400000,
                    "bytes": 148213,
                    "links": { "self": "https://service.mock/docs/88213" }
                  }
                ]
              },
              "meta": { "count": 1 }
            }
            """;

        var client = CreateServiceClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        var result = await client.FetchAsync(TestVin, CancellationToken.None);

        var ok = result.ShouldBeOfType<UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok>();
        ok.Value.ShouldHaveSingleItem().Id.ShouldBe("service:88213");
    }

    [Fact]
    public async Task Non_success_status_maps_to_HttpError_with_status_code()
    {
        var client = CreateSalesClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await client.FetchAsync(TestVin, CancellationToken.None);

        var failed = result.ShouldBeOfType<UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed>();
        failed.Failure.Reason.ShouldBe(UpstreamFailureReason.HttpError);
        failed.Failure.StatusCode.ShouldBe(503);
    }

    [Fact]
    public async Task Malformed_json_maps_to_InvalidPayload_without_throwing()
    {
        var client = CreateServiceClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{malformed", Encoding.UTF8, "application/json")
        });

        var result = await client.FetchAsync(TestVin, CancellationToken.None);

        var failed = result.ShouldBeOfType<UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed>();
        failed.Failure.Reason.ShouldBe(UpstreamFailureReason.InvalidPayload);
    }

    [Fact]
    public async Task TimeoutRejectedException_maps_to_Timeout()
    {
        var client = CreateSalesClient(_ => throw new TimeoutRejectedException("2s elapsed"));

        var result = await client.FetchAsync(TestVin, CancellationToken.None);

        var failed = result.ShouldBeOfType<UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed>();
        failed.Failure.Reason.ShouldBe(UpstreamFailureReason.Timeout);
    }

    [Fact]
    public async Task BrokenCircuitException_maps_to_CircuitOpen()
    {
        var client = CreateServiceClient(_ => throw new BrokenCircuitException("breaker open"));

        var result = await client.FetchAsync(TestVin, CancellationToken.None);

        var failed = result.ShouldBeOfType<UpstreamResult<IReadOnlyList<VehicleDocument>>.Failed>();
        failed.Failure.Reason.ShouldBe(UpstreamFailureReason.CircuitOpen);
    }

    [Fact]
    public async Task Caller_cancellation_rethrows_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var client = CreateSalesClient(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => client.FetchAsync(TestVin, cts.Token));
    }

    [Fact]
    public void AddDocumentSources_registers_both_isolated_sources()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Upstreams:SalesSystem:BaseAddress"] = "http://localhost:5101",
                ["Upstreams:ServiceSystem:BaseAddress"] = "http://localhost:5102"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentSources(config);

        using var provider = services.BuildServiceProvider();
        var sources = provider.GetServices<IDocumentSource>().ToList();

        sources.Select(s => s.System).ShouldBe([SourceSystem.SalesSystem, SourceSystem.ServiceSystem], ignoreOrder: true);
    }

    private static SalesSystemClient CreateSalesClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new StubHandler(handler)) { BaseAddress = new Uri("http://sales.test") };
        return new SalesSystemClient(http, new SalesDocumentNormalizer(), NullLogger<SalesSystemClient>.Instance);
    }

    private static ServiceSystemClient CreateServiceClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new StubHandler(handler)) { BaseAddress = new Uri("http://service.test") };
        return new ServiceSystemClient(http, new ServiceDocumentNormalizer(), NullLogger<ServiceSystemClient>.Instance);
    }

    private static Vin ParseVin(string raw)
    {
        Vin.TryParse(raw, out var vin, out _).ShouldBeTrue();
        return vin;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }
}
