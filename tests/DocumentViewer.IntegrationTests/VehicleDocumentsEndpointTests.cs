using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocumentViewer.Api.Contracts;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;

namespace DocumentViewer.IntegrationTests;

public sealed class VehicleDocumentsEndpointTests : IDisposable
{
    private static readonly Vin ValidVin = ParseVin("1HGCM82633A004352");
    private static readonly DateTimeOffset FixedTime = new(2026, 10, 30, 10, 42, 11, TimeSpan.Zero);

    private readonly IDocumentAggregationService _aggregation = Substitute.For<IDocumentAggregationService>();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public VehicleDocumentsEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDocumentAggregationService>();
                services.AddSingleton(_aggregation);
            });
        });

        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Returns_200_and_documents_when_all_sources_ok()
    {
        _aggregation.GetDocumentsAsync(ValidVin, Arg.Any<CancellationToken>())
            .Returns(new AggregationResult(
                ValidVin,
                FixedTime,
                Partial: false,
                Documents:
                [
                    new VehicleDocument("sales:PO-88213", SourceSystem.SalesSystem, DocumentType.PurchaseOrder, "Purchase order PO-88213", FixedTime, 148480, "https://sales.mock/documents/PO-88213"),
                    new VehicleDocument("service:88213", SourceSystem.ServiceSystem, DocumentType.JobCard, "Job card #88213", FixedTime, 148213, "https://service.mock/docs/88213")
                ],
                Sources:
                [
                    new SourceReport(SourceSystem.SalesSystem, SourceStatus.Ok, 1, 45, null),
                    new SourceReport(SourceSystem.ServiceSystem, SourceStatus.Ok, 1, 62, null)
                ]));

        var response = await _client.GetAsync($"/api/v1/vehicles/{ValidVin.Value}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DocumentsResponse>();
        body.ShouldNotBeNull();
        body.Vin.ShouldBe("1HGCM82633A004352");
        body.Partial.ShouldBeFalse();
        body.Documents.Count.ShouldBe(2);
        body.Sources.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Returns_200_with_partial_true_when_one_source_failed()
    {
        _aggregation.GetDocumentsAsync(ValidVin, Arg.Any<CancellationToken>())
            .Returns(new AggregationResult(
                ValidVin,
                FixedTime,
                Partial: true,
                Documents:
                [
                    new VehicleDocument("sales:PO-88213", SourceSystem.SalesSystem, DocumentType.PurchaseOrder, "Purchase order PO-88213", FixedTime, 148480, null)
                ],
                Sources:
                [
                    new SourceReport(SourceSystem.SalesSystem, SourceStatus.Ok, 1, 120, null),
                    new SourceReport(SourceSystem.ServiceSystem, SourceStatus.Failed, 0, 5002, new UpstreamFailure(UpstreamFailureReason.Timeout, "Total request timeout of 5s elapsed", null))
                ]));

        var response = await _client.GetAsync($"/api/v1/vehicles/{ValidVin.Value}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DocumentsResponse>();
        body.ShouldNotBeNull();
        body.Partial.ShouldBeTrue();
        body.Documents.ShouldHaveSingleItem();
        var failedSource = body.Sources.Single(s => s.System == SourceSystem.ServiceSystem);
        failedSource.Status.ShouldBe(SourceStatus.Failed);
        failedSource.Error.ShouldNotBeNull();
        failedSource.Error.Reason.ShouldBe(UpstreamFailureReason.Timeout);
    }

    [Fact]
    public async Task Returns_503_with_Retry_After_when_all_sources_failed()
    {
        _aggregation.GetDocumentsAsync(ValidVin, Arg.Any<CancellationToken>())
            .Returns(new AggregationResult(
                ValidVin,
                FixedTime,
                Partial: true,
                Documents: [],
                Sources:
                [
                    new SourceReport(SourceSystem.SalesSystem, SourceStatus.Failed, 0, 5001, new UpstreamFailure(UpstreamFailureReason.Timeout, "timeout", null)),
                    new SourceReport(SourceSystem.ServiceSystem, SourceStatus.Failed, 0, 52, new UpstreamFailure(UpstreamFailureReason.HttpError, "503", 503))
                ]));

        var response = await _client.GetAsync($"/api/v1/vehicles/{ValidVin.Value}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();
        response.Headers.RetryAfter.Delta.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Returns_200_with_empty_list_when_no_documents_exist()
    {
        var emptyVin = ParseVin("WAUZZZ8V1JA003421");
        _aggregation.GetDocumentsAsync(emptyVin, Arg.Any<CancellationToken>())
            .Returns(new AggregationResult(
                emptyVin,
                FixedTime,
                Partial: false,
                Documents: [],
                Sources:
                [
                    new SourceReport(SourceSystem.SalesSystem, SourceStatus.Ok, 0, 22, null),
                    new SourceReport(SourceSystem.ServiceSystem, SourceStatus.Ok, 0, 19, null)
                ]));

        var response = await _client.GetAsync($"/api/v1/vehicles/{emptyVin.Value}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DocumentsResponse>();
        body.ShouldNotBeNull();
        body.Documents.ShouldBeEmpty();
        body.Partial.ShouldBeFalse();
    }

    [Theory]
    [InlineData("TOO-SHORT")]
    [InlineData("1HGCM82633A0O4352")]
    [InlineData("1HGCM82633A004353")]
    public async Task Returns_400_problem_details_for_invalid_vin(string vin)
    {
        var response = await _client.GetAsync($"/api/v1/vehicles/{vin}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.ShouldNotBeNull();
        problem.Type.ShouldNotBeNull();
        problem.Type.ShouldEndWith("invalid-vin");
    }

    [Fact]
    public async Task Response_body_never_contains_a_lowercase_or_untrimmed_vin()
    {
        _aggregation.GetDocumentsAsync(ValidVin, Arg.Any<CancellationToken>())
            .Returns(new AggregationResult(
                ValidVin,
                FixedTime,
                Partial: false,
                Documents: [],
                Sources:
                [
                    new SourceReport(SourceSystem.SalesSystem, SourceStatus.Ok, 0, 10, null)
                ]));

        var response = await _client.GetAsync("/api/v1/vehicles/%201hgcm82633a004352%20/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain("1hgcm82633a004352", Case.Sensitive);
        raw.ShouldNotContain(" 1HGCM82633A004352 ", Case.Sensitive);
        raw.ShouldContain("\"vin\":\"1HGCM82633A004352\"", Case.Sensitive);
    }

    [Fact]
    public async Task Source_system_is_present_on_every_document()
    {
        _aggregation.GetDocumentsAsync(ValidVin, Arg.Any<CancellationToken>())
            .Returns(new AggregationResult(
                ValidVin,
                FixedTime,
                Partial: false,
                Documents:
                [
                    new VehicleDocument("sales:PO-1", SourceSystem.SalesSystem, DocumentType.PurchaseOrder, "PO", FixedTime, 100, null),
                    new VehicleDocument("service:88", SourceSystem.ServiceSystem, DocumentType.JobCard, "JC", FixedTime, 200, null)
                ],
                Sources:
                [
                    new SourceReport(SourceSystem.SalesSystem, SourceStatus.Ok, 1, 15, null),
                    new SourceReport(SourceSystem.ServiceSystem, SourceStatus.Ok, 1, 18, null)
                ]));

        var response = await _client.GetAsync($"/api/v1/vehicles/{ValidVin.Value}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var docs = json.RootElement.GetProperty("documents");
        docs.GetArrayLength().ShouldBe(2);
        foreach (var doc in docs.EnumerateArray())
        {
            doc.TryGetProperty("sourceSystem", out var srcProp).ShouldBeTrue();
            srcProp.GetString().ShouldBeOneOf("SalesSystem", "ServiceSystem");
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static Vin ParseVin(string raw)
    {
        Vin.TryParse(raw, out var vin, out _).ShouldBeTrue();
        return vin;
    }
}
