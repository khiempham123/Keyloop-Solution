using DocumentViewer.Domain;
using DocumentViewer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DocumentViewer.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class DocumentCacheRepositoryTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Upsert_then_get_round_trips_documents()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var repository = CreateRepository(clock);
        var vin = ParseVin("1HGCM82633A004352");
        var docs = new List<VehicleDocument>
        {
            new(
                "sales:PO-88213",
                SourceSystem.SalesSystem,
                DocumentType.PurchaseOrder,
                "Vehicle purchase order",
                BaseTime.AddDays(-5),
                148480,
                "https://sales.mock/documents/PO-88213"),
            new(
                "sales:FIN-99001",
                SourceSystem.SalesSystem,
                DocumentType.FinanceAgreement,
                "Finance agreement",
                null,
                null,
                null)
        };

        await repository.UpsertAsync(vin, SourceSystem.SalesSystem, docs, TimeSpan.FromMinutes(15), CancellationToken.None);

        var cached = await repository.GetAsync(vin, SourceSystem.SalesSystem, CancellationToken.None);

        cached.ShouldNotBeNull();
        cached.FetchedAt.ShouldBe(BaseTime);
        cached.Documents.Count.ShouldBe(2);
        cached.Documents[0].Id.ShouldBe("sales:PO-88213");
        cached.Documents[0].SourceSystem.ShouldBe(SourceSystem.SalesSystem);
        cached.Documents[0].DocumentType.ShouldBe(DocumentType.PurchaseOrder);
        cached.Documents[0].SizeBytes.ShouldBe(148480);
        cached.Documents[1].Id.ShouldBe("sales:FIN-99001");
        cached.Documents[1].IssuedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Get_returns_null_for_expired_entry()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var repository = CreateRepository(clock);
        var vin = ParseVin("1M8GDM9AXKP042788");

        await repository.UpsertAsync(vin, SourceSystem.ServiceSystem, Docs(1), TimeSpan.FromMinutes(15), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(1)));

        var cached = await repository.GetAsync(vin, SourceSystem.ServiceSystem, CancellationToken.None);

        cached.ShouldBeNull();
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_vin()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var repository = CreateRepository(clock);
        var unknownVin = ParseVin("WAUZZZ8V1JA003421");

        var cached = await repository.GetAsync(unknownVin, SourceSystem.SalesSystem, CancellationToken.None);

        cached.ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_overwrites_existing_entry_for_same_vin_and_source()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var repository = CreateRepository(clock);
        var vin = ParseVin("JH4KA7561PC008269");

        await repository.UpsertAsync(vin, SourceSystem.SalesSystem, Docs(1, batch: 1), TimeSpan.FromMinutes(15), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(2));
        var updatedDocs = Docs(3, batch: 2);
        await repository.UpsertAsync(vin, SourceSystem.SalesSystem, updatedDocs, TimeSpan.FromMinutes(15), CancellationToken.None);

        var cached = await repository.GetAsync(vin, SourceSystem.SalesSystem, CancellationToken.None);

        cached.ShouldNotBeNull();
        cached.FetchedAt.ShouldBe(BaseTime.AddMinutes(2));
        cached.Documents.Count.ShouldBe(3);
        cached.Documents[0].Title.ShouldContain("batch-2");
    }

    [Fact]
    public async Task Concurrent_upserts_for_same_key_do_not_throw()
    {
        var clock = new MutableTimeProvider(BaseTime);
        var repository = CreateRepository(clock);
        var vin = ParseVin("1FTFW1ET7EFA00003");

        var tasks = Enumerable.Range(0, 8).Select(i =>
            repository.UpsertAsync(vin, SourceSystem.SalesSystem, Docs(2, batch: i), TimeSpan.FromMinutes(15), CancellationToken.None));

        await Should.NotThrowAsync(() => Task.WhenAll(tasks));

        var cached = await repository.GetAsync(vin, SourceSystem.SalesSystem, CancellationToken.None);
        cached.ShouldNotBeNull();
        cached.Documents.Count.ShouldBe(2);
    }

    private DocumentCacheRepository CreateRepository(TimeProvider clock) =>
        new(postgres.DbContextFactory, clock, NullLogger<DocumentCacheRepository>.Instance);

    private static IReadOnlyList<VehicleDocument> Docs(int count, int batch = 0) =>
        Enumerable.Range(1, count)
            .Select(i => new VehicleDocument(
                $"sales:DOC-{batch}-{i}",
                SourceSystem.SalesSystem,
                DocumentType.PurchaseOrder,
                $"Doc {i} batch-{batch}",
                BaseTime,
                1024 * i,
                $"https://sales.mock/docs/{batch}/{i}"))
            .ToList();

    private static Vin ParseVin(string raw)
    {
        Vin.TryParse(raw, out var vin, out var error).ShouldBeTrue($"VIN {raw} failed with {error}");
        return vin;
    }
}
