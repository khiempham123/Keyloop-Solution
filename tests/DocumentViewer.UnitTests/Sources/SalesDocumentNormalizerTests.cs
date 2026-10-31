using DocumentViewer.Domain;
using DocumentViewer.Infrastructure.Sources.Sales;
using Shouldly;

namespace DocumentViewer.UnitTests.Sources;

public sealed class SalesDocumentNormalizerTests
{
    private readonly SalesDocumentNormalizer _sut = new();

    [Fact]
    public void Maps_kilobytes_to_bytes()
    {
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("PO-88213", "PURCHASE_ORDER", "Purchase order PO-88213", "2026-03-04", 145, "https://sales.mock/documents/PO-88213")
        ]);

        var result = _sut.Normalize(payload);

        result.ShouldHaveSingleItem().SizeBytes.ShouldBe(148_480L);
    }

    [Theory]
    [InlineData(9_000_000_000_000_000_000L)]  // 9e18 KB — multiplying by 1024 overflows long
    [InlineData(long.MaxValue)]
    public void Reports_a_size_it_cannot_represent_as_unknown_rather_than_a_negative_number(long fileSizeKb)
    {
        // An upstream is untrusted input (spec §2, principle 3). Unchecked, kilobytes
        // times 1024 wraps round and the API hands the client a negative byte count —
        // a number that is worse than no number, because a client will believe it.
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("PO-1", "PURCHASE_ORDER", "Purchase order PO-1", "2026-03-04", fileSizeKb, null)
        ]);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.SizeBytes.ShouldBeNull();
        doc.Title.ShouldBe("Purchase order PO-1", "an unusable size must not cost us the document");
    }

    [Fact]
    public void Maps_the_largest_representable_size()
    {
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("PO-1", "PURCHASE_ORDER", "Purchase order PO-1", "2026-03-04", long.MaxValue / 1024, null)
        ]);

        _sut.Normalize(payload).ShouldHaveSingleItem().SizeBytes.ShouldNotBeNull();
    }

    [Fact]
    public void Maps_iso_date_to_utc_midnight()
    {
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("PO-88213", "PURCHASE_ORDER", "Purchase order PO-88213", "2026-03-04", 145, "https://sales.mock/documents/PO-88213")
        ]);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.IssuedAt.ShouldBe(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero));
        doc.IssuedAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Namespaces_id_with_source_prefix()
    {
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("PO-88213", "PURCHASE_ORDER", "Purchase order PO-88213", "2026-03-04", 145, "https://sales.mock/documents/PO-88213")
        ]);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.Id.ShouldBe("sales:PO-88213");
        doc.SourceSystem.ShouldBe(SourceSystem.SalesSystem);
    }

    [Fact]
    public void Maps_known_doctype()
    {
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("PO-1", "PURCHASE_ORDER", "Purchase order", "2026-03-04", 100, null),
            new SalesDocumentItem("FA-1", "FINANCE_AGREEMENT", "Finance agreement", "2026-03-05", 100, null),
            new SalesDocumentItem("RG-1", "REGISTRATION_DOCUMENT", "Registration", "2026-03-06", 100, null)
        ]);

        var docs = _sut.Normalize(payload);

        docs.Select(d => d.DocumentType).ShouldBe(
        [
            DocumentType.PurchaseOrder,
            DocumentType.FinanceAgreement,
            DocumentType.RegistrationDocument
        ]);
    }

    [Fact]
    public void Maps_unknown_doctype_to_Unknown_and_keeps_document()
    {
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("WAR-12008", "EXTENDED_WARRANTY_PACK", "Extended protection", "2026-03-12", 220, "https://sales.mock/documents/WAR-12008")
        ]);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.DocumentType.ShouldBe(DocumentType.Unknown);
        doc.Title.ShouldBe("Extended protection");
    }

    [Fact]
    public void Deduplicates_repeated_documentId_within_source()
    {
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("PO-88213", "PURCHASE_ORDER", "First copy", "2026-03-04", 145, null),
            new SalesDocumentItem("PO-88213", "PURCHASE_ORDER", "Duplicate copy", "2026-03-04", 145, null)
        ]);

        var docs = _sut.Normalize(payload);

        docs.ShouldHaveSingleItem().Title.ShouldBe("First copy");
    }

    [Fact]
    public void Skips_document_with_missing_description_rather_than_throwing()
    {
        var payload = new SalesDocumentsResponse(
        [
            new SalesDocumentItem("BAD-1", "PURCHASE_ORDER", null, "2026-03-04", 100, null),
            new SalesDocumentItem("BAD-2", "PURCHASE_ORDER", "   ", "2026-03-04", 100, null),
            new SalesDocumentItem("PO-88213", "PURCHASE_ORDER", "Valid purchase order", "not-a-date", null, null)
        ]);

        var doc = _sut.Normalize(payload).ShouldHaveSingleItem();

        doc.Id.ShouldBe("sales:PO-88213");
        doc.IssuedAt.ShouldBeNull();
        doc.SizeBytes.ShouldBeNull();
    }

    [Fact]
    public void Returns_empty_list_for_empty_payload()
    {
        _sut.Normalize(new SalesDocumentsResponse([])).ShouldBeEmpty();
        _sut.Normalize(new SalesDocumentsResponse(null)).ShouldBeEmpty();
    }
}
