using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfNavigationServiceTests
{
    private readonly PdfNavigationService _sut = new();

    [Fact]
    public async Task Outline_RoundTrip()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        PdfBookmark[] outline = [new("Capitolo 1", 0, [new PdfBookmark("Sezione 1.1", 1)]), new("Capitolo 2", 2)];

        pdf = await _sut.WriteOutlineAsync(pdf, outline);
        var back = await _sut.ReadOutlineAsync(pdf);

        Assert.Equal(["Capitolo 1", "Capitolo 2"], back.Select(b => b.Title));
        Assert.Equal([0, 2], back.Select(b => b.PageIndex));
        Assert.Equal(("Sezione 1.1", 1), (back[0].Children[0].Title, back[0].Children[0].PageIndex));
    }

    [Fact]
    public async Task Links_AddReadDelete()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        pdf = await _sut.AddLinkAsync(pdf, 0, new PdfRect(0.1, 0.1, 0.3, 0.15), 2, null);
        pdf = await _sut.AddLinkAsync(pdf, 0, new PdfRect(0.1, 0.2, 0.3, 0.25), null, "https://example.org");

        var links = await _sut.ReadLinksAsync(pdf);
        Assert.Equal([2, null], links.Select(l => l.TargetPage));
        Assert.Equal("https://example.org", links[1].Uri);

        pdf = await _sut.DeleteLinkAsync(pdf, 0, links[0].AnnotIndex);
        Assert.Single(await _sut.ReadLinksAsync(pdf));
    }

    [Fact]
    public async Task Stamp_WritesHeaderAndFooterText()
    {
        var pdf = await _sut.StampAsync(PdfFixtureBuilder.Create(2),
            [new PageStamp { PageIndex = 1, TopRight = "RISERVATO", BottomCenter = "Pagina 2 di 2", BottomRight = "ABC000002" }]);

        var text = await new PdfTextExtractor().ExtractTextAsync(pdf, 1);
        Assert.Contains("RISERVATO", text);
        Assert.Contains("ABC000002", text);
    }

    [Fact]
    public async Task BuildFromSession_MergeKeepsOutlineAndRetargetsLinks()
    {
        var a = await _sut.WriteOutlineAsync(PdfFixtureBuilder.Create(2), [new PdfBookmark("A2", 1)]);
        a = await _sut.AddLinkAsync(a, 0, new PdfRect(0.1, 0.1, 0.3, 0.15), 1, null);
        var b = await _sut.WriteOutlineAsync(PdfFixtureBuilder.Create(2), [new PdfBookmark("B1", 0)]);
        var session = new PdfDocumentSession();
        session.AddSource(b, 2);
        session.AddSource(a, 2); // output: B1 B2 A1 A2

        var built = await new PdfPageService().BuildFromSessionAsync(session);

        var outline = await _sut.ReadOutlineAsync(built);
        Assert.Equal([("B1", 0), ("A2", 3)], outline.Select(o => (o.Title, o.PageIndex)));
        var link = Assert.Single(await _sut.ReadLinksAsync(built));
        Assert.Equal((2, 3), (link.PageIndex, link.TargetPage));
        using var doc = PdfReader.Open(new MemoryStream(built), PdfDocumentOpenMode.Import);
        Assert.Equal(4, doc.PageCount); // no target pages dragged along by the import
    }

    [Fact]
    public async Task BuildFromSession_SingleSourceReorder_KeepsCatalogEntries()
    {
        var src = PdfFixtureBuilder.Create(3);
        using (var doc = PdfReader.Open(new MemoryStream(src), PdfDocumentOpenMode.Modify))
        {
            doc.Internals.Catalog.Elements.SetString("/Lang", "it-IT");
            using var ms = new MemoryStream();
            doc.Save(ms);
            src = ms.ToArray();
        }
        var session = new PdfDocumentSession();
        session.AddSource(src, 3);
        session.Reorder([2, 0, 1]);
        session.Rotate(0, PageRotation.Clockwise90);

        var built = await new PdfPageService().BuildFromSessionAsync(session);

        using var outDoc = PdfReader.Open(new MemoryStream(built), PdfDocumentOpenMode.Import);
        Assert.Equal("it-IT", outDoc.Internals.Catalog.Elements.GetString("/Lang"));
        Assert.Equal(90, outDoc.Pages[0].Rotate);
    }
}
