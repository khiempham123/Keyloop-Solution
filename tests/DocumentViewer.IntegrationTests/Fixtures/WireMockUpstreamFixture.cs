using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocumentViewer.IntegrationTests.Fixtures;

/// <summary>
/// Two scriptable stand-ins for the systems of record. Unlike the mock services in
/// <c>mocks/</c>, these can be told to be slow, broken, or to answer with nonsense on a
/// per-test basis, and they count the requests they received — which is the only way to
/// prove that a 404 was not retried.
/// </summary>
public sealed class WireMockUpstreamFixture : IDisposable
{
    private WireMockUpstreamFixture(WireMockServer sales, WireMockServer service)
    {
        Sales = sales;
        Service = service;
    }

    public WireMockServer Sales { get; }

    public WireMockServer Service { get; }

    public string SalesBaseAddress => Sales.Urls[0];

    public string ServiceBaseAddress => Service.Urls[0];

    public static WireMockUpstreamFixture Start() =>
        new(WireMockServer.Start(), WireMockServer.Start());

    public static string SalesPath(string vin) => $"/sales/vehicles/{vin}/documents";

    public static string ServicePath(string vin) => $"/service/v2/vehicles/{vin}/docs";

    public static object SalesPayload(int documentCount) => new
    {
        documents = Enumerable.Range(1, documentCount).Select(i => new
        {
            documentId = $"PO-{88000 + i}",
            docType = "PURCHASE_ORDER",
            description = $"Purchase order PO-{88000 + i}",
            createdDate = "2026-03-04",
            fileSizeKb = 145,
            url = $"https://sales.mock/documents/PO-{88000 + i}"
        }).ToArray()
    };

    public static object ServicePayload(int documentCount) => new
    {
        data = new
        {
            items = Enumerable.Range(1, documentCount).Select(i => new
            {
                id = 91000 + i,
                category = "job-card",
                name = $"Job card #{91000 + i}",
                timestamp = 1772582400000L,
                bytes = 148213,
                links = new { self = $"https://service.mock/docs/{91000 + i}" }
            }).ToArray()
        },
        meta = new { count = documentCount }
    };

    /// <summary>Answers correctly, optionally after a delay.</summary>
    public void StubHealthy(WireMockServer server, string path, object payload, TimeSpan? delay = null)
    {
        var response = Response.Create().WithStatusCode(200).WithBodyAsJson(payload);
        if (delay is not null)
        {
            response = response.WithDelay(delay.Value);
        }

        server.Given(Request.Create().WithPath(path).UsingGet()).RespondWith(response);
    }

    /// <summary>Answers with a status code, optionally after a delay.</summary>
    public void StubStatus(WireMockServer server, string path, int statusCode, TimeSpan? delay = null)
    {
        var response = Response.Create().WithStatusCode(statusCode).WithBody("{}");
        if (delay is not null)
        {
            response = response.WithDelay(delay.Value);
        }

        server.Given(Request.Create().WithPath(path).UsingGet()).RespondWith(response);
    }

    /// <summary>Answers 200 with a payload the normalizer cannot make sense of.</summary>
    public void StubGarbage(WireMockServer server, string path) =>
        server
            .Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{ \"documents\": [ this is not json } "));

    /// <summary>Fails the given number of times, then answers correctly for ever.</summary>
    public void StubFailingThenHealthy(
        WireMockServer server,
        string path,
        int failures,
        int failureStatusCode,
        object payload)
    {
        const string scenario = "transient";
        var state = "start";

        for (var attempt = 0; attempt < failures; attempt++)
        {
            var next = $"attempt-{attempt + 1}";
            var given = server
                .Given(Request.Create().WithPath(path).UsingGet())
                .InScenario(scenario)
                .WillSetStateTo(next);

            if (attempt > 0)
            {
                given = given.WhenStateIs(state);
            }

            given.RespondWith(Response.Create().WithStatusCode(failureStatusCode).WithBody("{}"));
            state = next;
        }

        server
            .Given(Request.Create().WithPath(path).UsingGet())
            .InScenario(scenario)
            .WhenStateIs(state)
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(payload));
    }

    public int RequestCount(WireMockServer server, string path) =>
        server.LogEntries.Count(entry =>
            string.Equals(entry.RequestMessage?.Path, path, StringComparison.Ordinal));

    public void Dispose()
    {
        Sales.Stop();
        Sales.Dispose();
        Service.Stop();
        Service.Dispose();
    }
}
