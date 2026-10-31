using System.Net;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Application.Observability;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace DocumentViewer.IntegrationTests;

/// <summary>
/// The whole-request timeout is the one outcome where the user got nothing at all, so it
/// is the one that most needs counting. Spec §7.5 defines the availability SLO from
/// <c>aggregation.duration{outcome}</c>, and §8.4 calls <c>search_audit</c> the only
/// durable record of user-visible degradation — both were blind to this path.
/// </summary>
public sealed class TimeoutAccountingTests
{
    private const string TestVin = "1HGCM82633A004352";

    [Fact]
    public async Task A_whole_request_timeout_is_counted_and_audited()
    {
        var metrics = new RecordingMetrics();
        var audit = new RecordingAudit();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Default"] = string.Empty }));

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDocumentAggregationService>();
                services.AddSingleton<IDocumentAggregationService, TimingOutAggregation>();
                services.RemoveAll<IAggregationMetrics>();
                services.AddSingleton<IAggregationMetrics>(metrics);
                services.RemoveAll<ISearchAuditRepository>();
                services.AddSingleton<ISearchAuditRepository>(audit);
            });
        });

        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/vehicles/{TestVin}/documents");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();

        metrics.Outcomes.ShouldContain("timeout", "the availability SLO cannot see what is never recorded");

        await audit.Written;
        var entry = audit.Entries.ShouldHaveSingleItem();
        entry.Vin.ShouldBe(TestVin);
        entry.Partial.ShouldBeTrue("nothing was delivered, which is the most partial result there is");
        entry.Sources.ShouldBeEmpty();
    }

    private sealed class TimingOutAggregation : IDocumentAggregationService
    {
        public Task<AggregationResult> GetDocumentsAsync(Vin vin, CancellationToken ct) =>
            throw new OperationCanceledException("whole-request budget elapsed");
    }

    private sealed class RecordingMetrics : IAggregationMetrics
    {
        private readonly List<string> outcomes = [];

        public IReadOnlyList<string> Outcomes
        {
            get { lock (outcomes) { return outcomes.ToList(); } }
        }

        public void RecordAggregation(string outcome, TimeSpan duration)
        {
            lock (outcomes) { outcomes.Add(outcome); }
        }

        public void RecordUpstream(SourceSystem system, SourceStatus status, TimeSpan duration) { }

        public void RecordPartial() { }

        public void RecordCacheLookup(string result) { }
    }

    private sealed class RecordingAudit : ISearchAuditRepository
    {
        private readonly TaskCompletionSource written =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<SearchAuditEntry> entries = [];

        public Task Written => written.Task;

        public IReadOnlyList<SearchAuditEntry> Entries
        {
            get { lock (entries) { return entries.ToList(); } }
        }

        public Task AppendAsync(SearchAuditEntry entry, CancellationToken ct)
        {
            lock (entries) { entries.Add(entry); }
            written.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
