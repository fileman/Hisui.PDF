using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Xunit;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace Hisui.Pdf.Core.Tests;

public class PdfWordExporterTests
{
    private readonly PdfiumRenderer _renderer = new();

    private PdfWordExporter Create(IOcrEngine? engine = null) =>
        new(_renderer, new PdfTextExtractor(), new PdfImageExtractor(),
            engine is null ? null : new PdfOcrService(_renderer, engine));

    [Fact]
    public async Task Export_ProducesDocxWithThePageText()
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 12);
            gfx.DrawString("Hello converted world", font, XBrushes.Black, new XPoint(50, 100));
            gfx.DrawString("Second paragraph here", font, XBrushes.Black, new XPoint(50, 200));
        }
        using var pdf = new MemoryStream();
        doc.Save(pdf);

        var docx = await Create().ExportAsync(pdf.ToArray());

        using var word = Open(docx);
        var text = word.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("Hello converted world", text);
        Assert.Contains("Second paragraph here", text);
        Assert.Equal(2, word.MainDocumentPart.Document.Body.Elements<Paragraph>().Count());
    }

    [Fact]
    public async Task Export_SetsThePdfPageSizeAndMarginsOnTheSection()
    {
        var docx = await Create().ExportAsync(PdfFixtureBuilder.CreateDigitalLetterWithStrips());

        using var word = Open(docx);
        var section = word.MainDocumentPart!.Document.Body!.Elements<SectionProperties>().Single();
        var size = section.GetFirstChild<PageSize>()!;
        Assert.InRange((int)size.Width!.Value, 11900, 11910);   // A4: 595.3 pt × 20
        Assert.InRange((int)size.Height!.Value, 16830, 16840);  // 841.9 pt × 20
        var margin = section.GetFirstChild<PageMargin>()!;
        Assert.InRange((int)margin.Left!.Value, 1300, 1500);    // text starts at 72 pt (1440 twips)
    }

    [Fact]
    public async Task Export_GluesImageStripsIntoOnePictureAtItsPlacedSize()
    {
        var docx = await Create().ExportAsync(PdfFixtureBuilder.CreateDigitalLetterWithStrips());

        using var word = Open(docx);
        var extent = Assert.Single(word.MainDocumentPart!.Document.Body!.Descendants<DW.Extent>());
        Assert.InRange(extent.Cx!.Value / 12700.0, 195, 205);   // drawn 200 pt wide
        Assert.InRange(extent.Cy!.Value / 12700.0, 145, 155);   // three 50 pt strips
        Assert.Single(word.MainDocumentPart.ImageParts);
    }

    [Fact]
    public async Task Export_KeepsAlignmentListItemsAndLinks()
    {
        var docx = await Create().ExportAsync(PdfFixtureBuilder.CreateDigitalLetterWithStrips());

        using var word = Open(docx);
        var body = word.MainDocumentPart!.Document.Body!;
        var paragraphs = body.Elements<Paragraph>().ToList();

        var date = paragraphs.Single(p => p.InnerText.Contains("Macerata, 30.10.2026"));
        Assert.Equal(JustificationValues.Right, date.ParagraphProperties!.Justification!.Val!.Value);

        Assert.Contains(paragraphs, p => p.InnerText.StartsWith("1. First item"));
        Assert.Contains(paragraphs, p => p.InnerText.StartsWith("2. Second item"));

        var link = Assert.Single(body.Descendants<Hyperlink>());
        Assert.Equal("office@example.org", link.InnerText);
        var rel = word.MainDocumentPart.HyperlinkRelationships.Single(r => r.Id == link.Id);
        Assert.Equal("mailto:office@example.org", rel.Uri.ToString());
    }

    [Fact]
    public async Task Export_ScannedPage_UsesOcrTextKeepsTheStampAndCropsTheLogo()
    {
        var engine = new FakeOcrEngine(
            ("OGGETTO:", new PdfRect(0.30, 0.40, 0.42, 0.415)),
            ("convocazione", new PdfRect(0.43, 0.40, 0.60, 0.415)),
            ("Cordiali", new PdfRect(0.30, 0.50, 0.40, 0.515)),
            ("saluti.", new PdfRect(0.41, 0.50, 0.49, 0.515)));

        var result = await Create(engine).ExportAsync(PdfFixtureBuilder.CreateScannedLetterWithStamp(), new WordExportOptions());

        Assert.Equal([0], result.OcrPages);
        Assert.Null(result.OcrUnavailableReason);
        using var word = Open(result.Docx);
        var paragraphs = word.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
        Assert.Contains(PdfFixtureBuilder.ScannedLetterStamp, paragraphs);   // the rotated stamp, in reading order
        Assert.Contains("OGGETTO: convocazione", paragraphs);
        Assert.Contains("Cordiali saluti.", paragraphs);

        // The logo is cropped from the page; the three scan strips are not copied.
        Assert.Single(word.MainDocumentPart.ImageParts);
        var extent = word.MainDocumentPart.Document.Body!.Descendants<DW.Extent>().Single();
        Assert.InRange(extent.Cx!.Value / 914400.0, 1.1, 1.5); // ≈ 1.2" square plus a little margin
    }

    [Fact]
    public async Task Export_WhenOcrIsUnavailable_FallsBackToThePageImageAndSaysWhy()
    {
        var result = await Create(new UnavailableOcrEngine()).ExportAsync(PdfFixtureBuilder.CreateScannedLetterWithStamp(), new WordExportOptions());

        Assert.Equal("no tessdata", result.OcrUnavailableReason);
        Assert.Empty(result.OcrPages);
        using var word = Open(result.Docx);
        // The strips are glued back into one full-page picture.
        Assert.Single(word.MainDocumentPart!.ImageParts);
    }

    private static WordprocessingDocument Open(byte[] docx) => WordprocessingDocument.Open(new MemoryStream(docx), false);

    private sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly (string Text, PdfRect Box)[] words;

        public FakeOcrEngine(params (string Text, PdfRect Box)[] words) => this.words = words;

        public Task<OcrPageResult> RecognizeAsync(byte[] pageImagePng, int pageIndex, OcrOptions options, CancellationToken ct = default) =>
            Task.FromResult(new OcrPageResult
            {
                PageIndex = pageIndex,
                Words = [.. words.Select(w => new OcrWord { Text = w.Text, BoundingBox = w.Box, Confidence = 90 })],
                Text = string.Join(' ', words.Select(w => w.Text)),
                MeanConfidence = 90,
            });
    }

    private sealed class UnavailableOcrEngine : IOcrEngine
    {
        public Task<OcrPageResult> RecognizeAsync(byte[] pageImagePng, int pageIndex, OcrOptions options, CancellationToken ct = default) =>
            throw new OcrUnavailableException("no tessdata");
    }
}
