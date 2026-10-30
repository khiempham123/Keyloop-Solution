using System.Net;
using System.Net.Http.Json;
using DocumentViewer.Api.Contracts;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using DocumentViewer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace DocumentViewer.IntegrationTests.Persistence;

public sealed class DatabaseOutageTests
{
    private const string TestVin = "1HGCM82633A004352";

    [Fact]
    public async Task Request_succeeds_with_full_documents_when_database_is_unreachable()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] =
                        "Host=127.0.0.1;Port=59999;Database=unreachable_db;Username=postgres;Password=postgres;Timeout=1;Command Timeout=1;Pooling=false"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDocumentSource>();
                services.AddSingleton<IDocumentSource>(new StubHealthySource(
                    SourceSystem.SalesSystem,
                    new VehicleDocument(
                        "sales:PO-100",
                        SourceSystem.SalesSystem,
                        DocumentType.PurchaseOrder,
                        "Purchase order #100",
                        DateTimeOffset.UtcNow,
                        4096,
                        "https://sales.mock/docs/100")));
                services.AddSingleton<IDocumentSource>(new StubHealthySource(
                    SourceSystem.ServiceSystem,
                    new VehicleDocument(
                        "service:200",
                        SourceSystem.ServiceSystem,
                        DocumentType.JobCard,
                        "Service job card #200",
                        DateTimeOffset.UtcNow,
                        8192,
                        "https://service.mock/docs/200")));
            });
        });

        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<IDocumentCacheRepository>().ShouldBeOfType<DocumentCacheRepository>();
        factory.Services.GetRequiredService<ISearchAuditRepository>().ShouldBeOfType<SearchAuditRepository>();

        var response = await client.GetAsync($"/api/v1/vehicles/{TestVin}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DocumentsResponse>();
        body.ShouldNotBeNull();
        body.Documents.Count.ShouldBe(2);
        body.Partial.ShouldBeFalse();
    }

    private sealed class StubHealthySource(SourceSystem system, VehicleDocument document) : IDocumentSource
    {
        public SourceSystem System => system;

        public Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct) =>
            Task.FromResult<UpstreamResult<IReadOnlyList<VehicleDocument>>>(
                new UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok([document], TimeSpan.FromMilliseconds(12)));
    }
}
