using System.Net;
using System.Text.Json;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace DocumentViewer.IntegrationTests;

/// <summary>
/// The client layer is this submission's stubbed deliverable, and `openapi.json` is its
/// contract. A contract that does not describe the server is worse than none: a client
/// generated from it with validation enabled fails on the happy path. This walks the
/// generated document's own `required` lists against a real response.
/// </summary>
public sealed class OpenApiContractTests
{
    private const string TestVin = "1HGCM82633A004352";

    [Fact]
    public async Task Every_property_the_contract_marks_required_is_present_in_a_real_response()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var contract = JsonDocument.Parse(
            await client.GetStringAsync("/openapi/v1.json"));
        var schemas = contract.RootElement
            .GetProperty("components")
            .GetProperty("schemas");

        var response = await client.GetAsync($"/api/v1/vehicles/{TestVin}/documents");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        AssertSatisfies(schemas, "DocumentsResponse", root);

        var document = root.GetProperty("documents").EnumerateArray().First();
        AssertSatisfies(schemas, "DocumentItemResponse", document);
        document.TryGetProperty("issuedAt", out _).ShouldBeFalse(
            "the serialiser omits nulls, which is exactly why these must not be required");

        foreach (var source in root.GetProperty("sources").EnumerateArray())
        {
            AssertSatisfies(schemas, "SourceItemResponse", source);
            if (source.TryGetProperty("error", out var error))
            {
                AssertSatisfies(schemas, "SourceErrorResponse", error);
            }
        }

        root.GetProperty("sources").EnumerateArray()
            .Any(s => !s.TryGetProperty("error", out _))
            .ShouldBeTrue("a healthy source carries no error object at all");
    }

    private static void AssertSatisfies(JsonElement schemas, string schemaName, JsonElement actual)
    {
        if (!schemas.TryGetProperty(schemaName, out var schema)
            || !schema.TryGetProperty("required", out var required))
        {
            return;
        }

        foreach (var property in required.EnumerateArray())
        {
            var name = property.GetString()!;
            actual.TryGetProperty(name, out _).ShouldBeTrue(
                $"{schemaName}.required lists '{name}', but the server's response does not contain it");
        }
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = string.Empty }));

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDocumentSource>();
                // A document with every optional member absent, and one healthy source,
                // so the response exercises both omission cases at once.
                services.AddSingleton<IDocumentSource>(new StubSource(
                    SourceSystem.SalesSystem,
                    new VehicleDocument(
                        "sales:PO-1",
                        SourceSystem.SalesSystem,
                        DocumentType.PurchaseOrder,
                        "Purchase order PO-1",
                        IssuedAt: null,
                        SizeBytes: null,
                        DownloadUrl: null)));
            });
        });

    private sealed class StubSource(SourceSystem system, VehicleDocument document) : IDocumentSource
    {
        public SourceSystem System => system;

        public Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct) =>
            Task.FromResult<UpstreamResult<IReadOnlyList<VehicleDocument>>>(
                new UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok([document], TimeSpan.FromMilliseconds(7)));
    }
}
