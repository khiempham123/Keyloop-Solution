using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using DocumentViewer.Api.Contracts;
using DocumentViewer.Application.Sources;
using DocumentViewer.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace DocumentViewer.IntegrationTests.Persistence;

/// <summary>
/// Guards the defect found against live Supabase: a cold cross-region pool made every
/// first database operation pay the TLS + Supavisor + SASL handshake, which blew the
/// readiness budget and silently killed the stale-cache fallback.
///
/// A closed port is refused instantly and would prove nothing, so these tests point the
/// database at a black-hole listener that accepts the TCP connection and then never
/// answers — which is what a cold pooler looks like from the client's side.
/// </summary>
public sealed class DatabaseWarmupTests
{
    private const string TestVin = "1HGCM82633A004352";

    [Fact]
    public async Task A_database_that_accepts_but_never_answers_does_not_hold_up_a_document_read()
    {
        using var blackHole = BlackHoleListener.Start();
        await using var factory = CreateFactory(blackHole, readinessTimeoutMs: null);
        using var client = factory.CreateClient();

        var sw = Stopwatch.StartNew();
        var response = await client.GetAsync($"/api/v1/vehicles/{TestVin}/documents");
        sw.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DocumentsResponse>();
        body.ShouldNotBeNull();
        body.Documents.Count.ShouldBe(1);
        body.Partial.ShouldBeFalse();
        sw.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(3),
            "cache and audit writes must never sit on the response path");
    }

    [Fact]
    public async Task Readiness_budget_comes_from_configuration_not_a_hardcoded_two_seconds()
    {
        using var blackHole = BlackHoleListener.Start();
        await using var factory = CreateFactory(blackHole, readinessTimeoutMs: 200);
        using var client = factory.CreateClient();

        var sw = Stopwatch.StartNew();
        var ready = await client.GetAsync("/health/ready");
        sw.Stop();

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        sw.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(1.5),
            "a configured 200ms budget must be honoured instead of the old hardcoded 2s");
    }

    [Fact]
    public async Task A_generous_readiness_budget_is_also_honoured()
    {
        using var blackHole = BlackHoleListener.Start();
        await using var factory = CreateFactory(blackHole, readinessTimeoutMs: 3000);
        using var client = factory.CreateClient();

        var sw = Stopwatch.StartNew();
        var ready = await client.GetAsync("/health/ready");
        sw.Stop();

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        sw.Elapsed.ShouldBeGreaterThan(
            TimeSpan.FromSeconds(2.5),
            "the old hardcoded 2s ceiling must no longer cap a longer configured budget");
    }

    [Fact]
    public async Task Readiness_is_healthy_when_no_database_is_configured_at_all()
    {
        // Running without persistence is a supported configuration: the service answers
        // document searches, it just has no cache or audit trail. Spec §7.4 defines
        // readiness as "I can do my job", so a deliberate absence is not a failure —
        // only a database that was configured and cannot be reached is.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = string.Empty }));
        });
        using var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        BlackHoleListener blackHole,
        int? readinessTimeoutMs) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    // Timeout=30 keeps Npgsql itself from giving up, so the budget under
                    // test is the only thing that can end the attempt.
                    ["ConnectionStrings:Default"] =
                        $"Host=127.0.0.1;Port={blackHole.Port};Database=black_hole;" +
                        "Username=postgres;Password=postgres;Timeout=30;Pooling=false"
                };

                if (readinessTimeoutMs is not null)
                {
                    settings["HealthChecks:DatabaseTimeoutMs"] = readinessTimeoutMs.Value.ToString();
                }

                config.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDocumentSource>();
                services.AddSingleton<IDocumentSource>(new StubSource(
                    SourceSystem.SalesSystem,
                    new VehicleDocument(
                        "sales:PO-100",
                        SourceSystem.SalesSystem,
                        DocumentType.PurchaseOrder,
                        "Purchase order #100",
                        DateTimeOffset.UtcNow,
                        4096,
                        "https://sales.mock/docs/100")));
            });
        });

    /// <summary>Accepts TCP connections and then says nothing, for ever.</summary>
    private sealed class BlackHoleListener : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();
        private readonly List<TcpClient> accepted = [];

        private BlackHoleListener(TcpListener listener)
        {
            this.listener = listener;
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public static BlackHoleListener Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new BlackHoleListener(listener);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    lock (accepted)
                    {
                        accepted.Add(client);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // listener disposed
            }
            catch (SocketException)
            {
                // listener disposed
            }
            catch (ObjectDisposedException)
            {
                // listener disposed
            }
        }

        public void Dispose()
        {
            cts.Cancel();
            lock (accepted)
            {
                foreach (var client in accepted)
                {
                    client.Dispose();
                }
                accepted.Clear();
            }

            listener.Stop();
            cts.Dispose();
        }
    }

    private sealed class StubSource(SourceSystem system, VehicleDocument document) : IDocumentSource
    {
        public SourceSystem System => system;

        public Task<UpstreamResult<IReadOnlyList<VehicleDocument>>> FetchAsync(Vin vin, CancellationToken ct) =>
            Task.FromResult<UpstreamResult<IReadOnlyList<VehicleDocument>>>(
                new UpstreamResult<IReadOnlyList<VehicleDocument>>.Ok([document], TimeSpan.FromMilliseconds(9)));
    }
}
