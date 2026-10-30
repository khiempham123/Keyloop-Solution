using DocumentViewer.Domain;
using DocumentViewer.Infrastructure.Sources.Service;
using Shouldly;

namespace DocumentViewer.UnitTests.Sources;

public sealed class ServiceDocumentNormalizerTests
{
    private readonly ServiceDocumentNormalizer _sut = new();

    [Fact]
    public void Maps_epoch_millis_to_utc()
    {
        var payload = new ServiceDocumentsResponse(
            new ServiceDataPayload(
            [
                new ServiceDocumentItem(88213, "job-card", "Job card #88213", 1772582400000L, 148213L, new ServiceLinksPayload("https://service.mock/docs/88213"))
            ]),
            new ServiceMetaPayload(1));

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.IssuedAt.ShouldBe(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero));
        doc.IssuedAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Reads_url_from_nested_links_self()
    {
        var payload = new ServiceDocumentsResponse(
            new ServiceDataPayload(
            [
                new ServiceDocumentItem(88213, "job-card", "Job card #88213", 1772582400000L, 148213L, new ServiceLinksPayload("https://service.mock/docs/88213"))
            ]),
            null);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.DownloadUrl.ShouldBe("https://service.mock/docs/88213");
    }

    [Fact]
    public void Maps_bytes_directly()
    {
        var payload = new ServiceDocumentsResponse(
            new ServiceDataPayload(
            [
                new ServiceDocumentItem(88213, "job-card", "Job card #88213", 1772582400000L, 148213L, null)
            ]),
            null);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.SizeBytes.ShouldBe(148_213L);
    }

    [Fact]
    public void Namespaces_integer_id()
    {
        var payload = new ServiceDocumentsResponse(
            new ServiceDataPayload(
            [
                new ServiceDocumentItem(88213, "job-card", "Job card #88213", 1772582400000L, 148213L, null)
            ]),
            null);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.Id.ShouldBe("service:88213");
        doc.SourceSystem.ShouldBe(SourceSystem.ServiceSystem);
        doc.DocumentType.ShouldBe(DocumentType.JobCard);
    }

    [Fact]
    public void Maps_unknown_category_to_Unknown_and_keeps_document()
    {
        var payload = new ServiceDocumentsResponse(
            new ServiceDataPayload(
            [
                new ServiceDocumentItem(95501, "recall-bulletin", "Manufacturer campaign check #95501", 1773532800000L, 87412L, null)
            ]),
            null);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.DocumentType.ShouldBe(DocumentType.Unknown);
        doc.Title.ShouldBe("Manufacturer campaign check #95501");
    }

    [Fact]
    public void Tolerates_null_timestamp_as_null_IssuedAt()
    {
        var payload = new ServiceDocumentsResponse(
            new ServiceDataPayload(
            [
                new ServiceDocumentItem(91044, "inspection-report", "Inspection #91044", null, null, null)
            ]),
            null);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.IssuedAt.ShouldBeNull();
        doc.SizeBytes.ShouldBeNull();
        doc.DocumentType.ShouldBe(DocumentType.InspectionReport);
    }

    [Fact]
    public void Returns_empty_list_when_items_missing()
    {
        _sut.Normalize(new ServiceDocumentsResponse(null, null)).ShouldBeEmpty();
        _sut.Normalize(new ServiceDocumentsResponse(new ServiceDataPayload(null), null)).ShouldBeEmpty();
        _sut.Normalize(new ServiceDocumentsResponse(new ServiceDataPayload([]), null)).ShouldBeEmpty();
    }
}
