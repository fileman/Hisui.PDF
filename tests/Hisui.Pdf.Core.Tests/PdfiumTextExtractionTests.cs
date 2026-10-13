using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

using Xunit;

namespace Hisui.Pdf.Core.Tests;

/// <summary>
/// PDFium-backed text extraction against the PdfPig results it replaced (PdfPig is used here only as an oracle),
/// plus phrase search, orientation, non-Latin text and sharing the renderer's open document.
/// </summary>
public class PdfiumTextExtractionTests
{
    private static readonly string[] Lines =
    [
        "Il verbale di",
        "accordo finale è stato firmato",
        "Perché la città è già così: Più Qualità",
    ];

    private static byte[] TextPdf(int rotate = 0, XRect? crop = null, string[]? lines = null)
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 14);
            var y = 120.0;
            foreach (var line in lines ?? Lines)
            {
                gfx.DrawString(line, font, XBrushes.Black, new XPoint(70, y));
                y += 26;
            }
        }
        if (rotate != 0) page.Rotate = rotate;
        if (crop is { } c) page.CropBox = new PdfSharp.Pdf.PdfRectangle(new XPoint(c.Left, c.Top), new XPoint(c.Right, c.Bottom));
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Rotated(byte[] pdf, int degrees)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        doc.Pages[0].Rotate = degrees;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>What the PdfPig-based extractor produced: words read from an unrotated copy, boxes rotated back.</summary>
    private static List<(string Text, PdfRect Box)> PdfPigOracle(byte[] pdf, int rotate)
    {
        var flat = Rotated(pdf, 0);
        using var doc = UglyToad.PdfPig.PdfDocument.Open(flat);
        var page = doc.GetPage(1);
        return page.GetWords().Select(w => (w.Text, Box: new PdfRect(
            w.BoundingBox.Left / page.Width, (page.Height - w.BoundingBox.Top) / page.Height,
            w.BoundingBox.Right / page.Width, (page.Height - w.BoundingBox.Bottom) / page.Height).RotateClockwise(rotate))).ToList();
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task WordBoxes_AgreeWithPdfPig_OnRotatedPages(int rotate)
    {
        var pdf = Rotated(TextPdf(), rotate);
        var oracle = PdfPigOracle(pdf, rotate);
        var words = await new PdfTextExtractor().ExtractWordsAsync(pdf, 0);

        Assert.NotEmpty(oracle);
        Assert.Equal(oracle.Count, words.Count);
        Assert.Equal(oracle.Select(o => o.Text).Order(), words.Select(w => w.Text).Order());

        // PdfPig sorts words differently from content order, so pair each oracle word with a box that agrees.
        const double tol = 0.012;
        var remaining = words.ToList();
        foreach (var (text, e) in oracle)
        {
            var match = remaining.FirstOrDefault(w => w.Text == text
                && Math.Abs(w.BoundingBox.Left - e.Left) <= tol && Math.Abs(w.BoundingBox.Right - e.Right) <= tol
                && Math.Abs(w.BoundingBox.Top - e.Top) <= tol && Math.Abs(w.BoundingBox.Bottom - e.Bottom) <= tol);
            Assert.True(match is not null, $"rotate {rotate}: no PDFium word '{text}' near {e}");
            remaining.Remove(match!);
        }
        Assert.All(words, w => Assert.InRange(w.FontSizePoints, 13, 15));
    }

    [Fact]
    public async Task Search_PhraseSpansLineBreak_WithOneBoxPerWord()
    {
        var pdf = TextPdf();
        var hit = Assert.Single(await new PdfTextExtractor().SearchAsync(pdf, "di accordo"));
        Assert.Equal("di accordo", hit.MatchedText);
        Assert.Equal(2, hit.Boxes.Count);
        Assert.True(hit.Boxes[1].Top > hit.Boxes[0].Bottom - 0.005, "second word sits on the next line");
    }

    [Fact]
    public async Task Search_RespectsCaseAndWholeWordOptions()
    {
        var sut = new PdfTextExtractor();
        var pdf = TextPdf();

        Assert.Single(await sut.SearchAsync(pdf, "VERBALE"));
        Assert.Empty(await sut.SearchAsync(pdf, "VERBALE", new TextSearchOptions { MatchCase = true }));
        Assert.Single(await sut.SearchAsync(pdf, "verbale", new TextSearchOptions { MatchCase = true }));
        Assert.Single(await sut.SearchAsync(pdf, "accord"));
        Assert.Empty(await sut.SearchAsync(pdf, "accord", new TextSearchOptions { WholeWord = true }));
        Assert.Single(await sut.SearchAsync(pdf, "accordo", new TextSearchOptions { WholeWord = true }));
    }

    [Fact]
    public async Task SearchPattern_FindsRegexMatchesWithBoxes()
    {
        var pdf = TextPdf(lines: ["Scrivere a mario.rossi@example.com oggi"]);
        var hit = Assert.Single(await new PdfTextExtractor().SearchPatternAsync(pdf,
            new System.Text.RegularExpressions.Regex(@"[\w.]+@[\w.]+\.\w+")));
        Assert.Equal("mario.rossi@example.com", hit.MatchedText);
        Assert.Single(hit.Boxes);
    }

    [Fact]
    public async Task ItalianAccents_RoundTrip_ThroughTextWordsAndSearch()
    {
        var sut = new PdfTextExtractor();
        var pdf = TextPdf();

        var text = await sut.ExtractTextAsync(pdf, 0);
        Assert.Contains("Perché la città è già così", text);

        var words = await sut.ExtractWordsAsync(pdf, 0);
        Assert.Contains(words, w => w.Text == "città");
        Assert.Contains(words, w => w.Text == "Qualità");

        Assert.Single(await sut.SearchAsync(pdf, "così: più qualità"));
    }

    [Fact]
    public async Task ExtractText_MultiplePages_AreSeparatedByFormFeed()
    {
        var pdf = Fixtures.PdfFixtureBuilder.CreateWithText("Pagina", 3);
        var text = await new PdfTextExtractor().ExtractTextAsync(pdf);
        var pages = text.Split('\f');
        Assert.Equal(3, pages.Length);
        Assert.Contains("Pagina (p2)", pages[1]);
        Assert.Equal(pages[2], await new PdfTextExtractor().ExtractTextAsync(pdf, 2));
    }

    private static byte[] VerticalHeaderPdf(int pageRotate)
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 9);
            var state = gfx.Save();
            gfx.RotateAtTransform(-90, new XPoint(200, 300));
            gfx.DrawString("Vaso 1 litro", font, XBrushes.Black, new XPoint(200, 300));
            gfx.Restore(state);
            gfx.DrawString("Acero", font, XBrushes.Black, new XPoint(60, 330));
        }
        page.Rotate = pageRotate;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task VerticalText_IsOneNonHorizontalLine_AndHorizontalStaysWords()
    {
        var pages = await new PdfTextExtractor().ExtractAllWordsAsync(VerticalHeaderPdf(0));
        var vertical = Assert.Single(pages[0], w => !w.IsHorizontal);
        Assert.Equal("Vaso 1 litro", vertical.Text);
        Assert.True(vertical.BoundingBox.Height > vertical.BoundingBox.Width * 2, "tall, narrow box");
        var horizontal = Assert.Single(pages[0], w => w.IsHorizontal);
        Assert.Equal("Acero", horizontal.Text);
    }

    [Fact]
    public async Task Orientation_IsJudgedInRenderedOrientation()
    {
        // Text that runs up the raw page reads left-to-right once the page is rotated 90° clockwise.
        var pages = await new PdfTextExtractor().ExtractAllWordsAsync(VerticalHeaderPdf(90));
        Assert.Contains(pages[0], w => w.IsHorizontal && w.Text == "Vaso");
        Assert.Contains(pages[0], w => w.IsHorizontal && w.Text == "litro");
        var sideways = Assert.Single(pages[0], w => !w.IsHorizontal);
        Assert.Equal("Acero", sideways.Text);
    }

    [Fact]
    public async Task Search_FindsVerticalText()
    {
        var hit = Assert.Single(await new PdfTextExtractor().SearchAsync(VerticalHeaderPdf(0), "vaso 1 litro"));
        Assert.Equal(3, hit.Boxes.Count);
    }

    private static PdfRect InkBox(SKBitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 200 && c.Green > 200 && c.Blue > 200) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return new PdfRect((double)minX / bmp.Width, (double)minY / bmp.Height, (double)(maxX + 1) / bmp.Width, (double)(maxY + 1) / bmp.Height);
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task WordBoxes_FollowTheCropBox(int rotate)
    {
        // The crop box is offset from the origin, so page space and the rendered page no longer share a corner.
        var pdf = TextPdf(rotate, new XRect(40, 550, 360, 230));
        var words = await new PdfTextExtractor().ExtractWordsAsync(pdf, 0);
        var union = new PdfRect(words.Min(w => w.BoundingBox.Left), words.Min(w => w.BoundingBox.Top),
                                words.Max(w => w.BoundingBox.Right), words.Max(w => w.BoundingBox.Bottom));

        using var bmp = SKBitmap.Decode(await new PdfiumRenderer().RenderPageToPngAsync(pdf, 0, 72));
        var ink = InkBox(bmp);
        const double tol = 0.025;
        Assert.InRange(union.Left, ink.Left - tol, ink.Left + tol);
        Assert.InRange(union.Top, ink.Top - tol, ink.Top + tol);
        Assert.InRange(union.Right, ink.Right - tol, ink.Right + tol);
        Assert.InRange(union.Bottom, ink.Bottom - tol, ink.Bottom + tol);
    }

    [Fact]
    public async Task OutOfRangePage_Throws_AndEmptyPagesYieldNothing()
    {
        var sut = new PdfTextExtractor();
        var blank = Fixtures.PdfFixtureBuilder.Create(2);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.ExtractWordsAsync(blank, 5));
        Assert.All(await sut.ExtractAllWordsAsync(blank), p => Assert.Empty(p));
    }

    [Fact]
    public async Task ConcurrentExtractionAndRendering_OnTheSameBuffer_DoNotInterfere()
    {
        var renderer = new PdfiumRenderer();
        var sut = new PdfTextExtractor(renderer);
        var pdf = Fixtures.PdfFixtureBuilder.CreateWithText("Concorrenza città", 6);
        var expected = await sut.ExtractTextAsync(pdf);

        var jobs = Enumerable.Range(0, 24).Select(i => (i % 4) switch
        {
            0 => Task.Run(async () => Assert.Equal(expected, await sut.ExtractTextAsync(pdf))),
            1 => Task.Run(async () => Assert.Equal(6, (await sut.ExtractAllWordsAsync(pdf)).Count)),
            2 => Task.Run(async () => Assert.NotEmpty(await renderer.RenderPageToPngAsync(pdf, i % 6, 60))),
            _ => Task.Run(async () => Assert.Equal(6, (await sut.SearchAsync(pdf, "concorrenza città")).Count)),
        }).ToArray();
        await Task.WhenAll(jobs);

        Assert.Equal(1, renderer.OpenDocumentCount); // one open document served both renders and text reads
    }
}
