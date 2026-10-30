using System.Text.Json;
using DocumentViewer.Application.Aggregation;
using DocumentViewer.Application.Persistence;
using DocumentViewer.Domain;
using DocumentViewer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DocumentViewer.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class SearchAuditRepositoryTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Audit_append_persists_full_vin_and_source_report()
    {
        var repository = new SearchAuditRepository(postgres.DbContextFactory, NullLogger<SearchAuditRepository>.Instance);
        var vin = "1HGCM82633A004352";
        var correlationId = $"corr-{Guid.NewGuid():N}";
        var entry = new SearchAuditEntry(
            Vin: vin,
            CorrelationId: correlationId,
            SearchedAt: BaseTime,
            DurationMs: 184,
            Partial: true,
            Sources:
            [
                new SourceReport(SourceSystem.SalesSystem, SourceStatus.Ok, 3, 48, null),
                new SourceReport(
                    SourceSystem.ServiceSystem,
                    SourceStatus.Failed,
                    0,
                    183,
                    new UpstreamFailure(UpstreamFailureReason.Timeout, "Per-attempt timeout exceeded", null))
            ]);

        await repository.AppendAsync(entry, CancellationToken.None);

        await using var db = await postgres.DbContextFactory.CreateDbContextAsync();
        var row = await db.SearchAudits.SingleAsync(a => a.CorrelationId == correlationId);

        row.Vin.ShouldBe(vin);
        row.DurationMs.ShouldBe(184);
        row.Partial.ShouldBeTrue();
        row.SearchedAt.ShouldBe(BaseTime);

        using var json = JsonDocument.Parse(row.SourceReport);
        json.RootElement.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Audit_rows_are_queryable_by_vin_most_recent_first()
    {
        var repository = new SearchAuditRepository(postgres.DbContextFactory, NullLogger<SearchAuditRepository>.Instance);
        var targetVin = "1M8GDM9AXKP042788";
        var otherVin = "WAUZZZ8V1JA003421";
        var sources = new[]
        {
            new SourceReport(SourceSystem.SalesSystem, SourceStatus.Ok, 1, 20, null)
        };

        await repository.AppendAsync(new SearchAuditEntry(targetVin, "corr-t0", BaseTime, 50, false, sources), CancellationToken.None);
        await repository.AppendAsync(new SearchAuditEntry(targetVin, "corr-t5", BaseTime.AddMinutes(5), 55, false, sources), CancellationToken.None);
        await repository.AppendAsync(new SearchAuditEntry(targetVin, "corr-t2", BaseTime.AddMinutes(2), 52, false, sources), CancellationToken.None);
        await repository.AppendAsync(new SearchAuditEntry(otherVin, "corr-other", BaseTime.AddMinutes(10), 40, false, sources), CancellationToken.None);

        await using var db = await postgres.DbContextFactory.CreateDbContextAsync();
        var rows = await db.SearchAudits
            .Where(a => a.Vin == targetVin)
            .OrderByDescending(a => a.SearchedAt)
            .ToListAsync();

        rows.Count.ShouldBeGreaterThanOrEqualTo(3);
        var matching = rows.Where(r => r.CorrelationId is "corr-t0" or "corr-t2" or "corr-t5").ToList();
        matching.Select(r => r.CorrelationId).ShouldBe(["corr-t5", "corr-t2", "corr-t0"]);
    }
}
