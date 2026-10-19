using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfTextEditServiceTests
{
    private readonly PdfTextEditService _sut = new();

    [Fact]
    public async Task ReplaceWord_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World");
        var bounds = new PdfRect(0.05, 0.1, 0.5, 0.15); // approximate normalized rect

        var result = await _sut.ReplaceWordAsync(pdf, 0, bounds, "Replaced", 14);

        Assert.True(IsValidPdf(result));
    }

    [Fact]
    public async Task ReplaceWord_PreservesPageCount()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Test", 2);
        var bounds = new PdfRect(0.05, 0.05, 0.3, 0.12);

        var result = await _sut.ReplaceWordAsync(pdf, 0, bounds, "New", 12);

        Assert.Equal(2, PageCount(result));
    }

    [Fact]
    public async Task ReplaceWord_ThrowsForOutOfRangePage()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Test", 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _sut.ReplaceWordAsync(pdf, 5, new PdfRect(0, 0, 0.1, 0.05), "x", 12));
    }

    [Fact]
    public async Task ReplaceWord_ThrowsForEmptyText()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Test");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.ReplaceWordAsync(pdf, 0, new PdfRect(0, 0, 0.1, 0.05), "", 12));
    }

    // ── find and replace (planned by the real extractor, so the glyphs must really go) ─────────

    private readonly PdfTextExtractor _extractor = new();

    private async Task<byte[]> ReplaceAllAsync(byte[] pdf, string query, string replacement)
    {
        var edits = new List<TextReplacement>();
        for (var page = 0; page < PageCount(pdf); page++)
            edits.AddRange(await _extractor.PlanReplacementsAsync(pdf, page, query, replacement));
        return await _sut.ApplyReplacementsAsync(pdf, edits);
    }

    [Fact]
    public async Task ApplyReplacements_RemovesOldTextAndAddsNew()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World");

        var result = await ReplaceAllAsync(pdf, "World", "Earth");

        var text = await _extractor.ExtractTextAsync(result);
        Assert.Contains("Earth", text);
        Assert.Contains("Hello", text);
        Assert.DoesNotContain("World", text); // gone from the content stream, not just painted over
        Assert.Empty(await _extractor.SearchAsync(result, "World"));
    }

    [Fact]
    public async Task ApplyReplacements_KeepsNeighbouringWordsInPlace()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World");
        var before = (await _extractor.ExtractWordsAsync(pdf, 0)).Single(w => w.Text == "Hello").BoundingBox;

        var result = await ReplaceAllAsync(pdf, "World", "X");

        var after = (await _extractor.ExtractWordsAsync(result, 0)).Single(w => w.Text == "Hello").BoundingBox;
        Assert.Equal(before.Left, after.Left, 3);
        Assert.Equal(before.Top, after.Top, 3);
        // "(p1)" after the removed word keeps its position too.
        var tailBefore = (await _extractor.ExtractWordsAsync(pdf, 0)).Single(w => w.Text == "(p1)").BoundingBox;
        var tailAfter = (await _extractor.ExtractWordsAsync(result, 0)).Single(w => w.Text == "(p1)").BoundingBox;
        Assert.Equal(tailBefore.Left, tailAfter.Left, 3);
    }

    [Fact]
    public async Task ApplyReplacements_SubstringRewritesTheWord()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello World");

        var result = await ReplaceAllAsync(pdf, "ell", "ipp");

        var text = await _extractor.ExtractTextAsync(result);
        Assert.Contains("Hippo", text);
        Assert.DoesNotContain("Hello", text);
    }

    [Fact]
    public async Task ApplyReplacements_EveryPage()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("alpha beta", 3);

        var result = await ReplaceAllAsync(pdf, "beta", "gamma");

        Assert.Equal(3, PageCount(result));
        Assert.Equal(3, (await _extractor.SearchAsync(result, "gamma")).Count);
        Assert.Empty(await _extractor.SearchAsync(result, "beta"));
    }

    [Theory]
    [InlineData(PdfFontEncoding.WinAnsi)]   // simple TrueType font: one byte per code, /Widths
    [InlineData(PdfFontEncoding.Unicode)]   // Type0 Identity-H: two bytes per code, /W
    public async Task ApplyReplacements_RemovesGlyphsInEitherFontKind(PdfFontEncoding encoding)
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        byte[] pdf;
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page))
                gfx.DrawString("keep remove keep", new XFont("Arial", 12, XFontStyleEx.Regular, new XPdfFontOptions(encoding)),
                    XBrushes.Black, new XPoint(60, 120));
            using var ms = new MemoryStream();
            document.Save(ms);
            pdf = ms.ToArray();
        }

        var result = await ReplaceAllAsync(pdf, "remove", "new");

        var words = (await _extractor.ExtractWordsAsync(result, 0)).Select(w => w.Text).ToList();
        Assert.DoesNotContain("remove", words);
        Assert.Equal(2, words.Count(w => w == "keep"));
        Assert.Contains("new", words);
    }

    [Fact]
    public async Task ApplyReplacements_RetypesInTheOriginalColourAndWeight()
    {
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        byte[] pdf;
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                gfx.DrawRectangle(XBrushes.Black, 0, 0, page.Width.Point, 200);
                gfx.DrawString("Dark heading", new XFont("Arial", 20, XFontStyleEx.Bold), XBrushes.White, new XPoint(60, 120));
            }
            using var ms = new MemoryStream();
            document.Save(ms);
            pdf = ms.ToArray();
        }
        var before = (await _extractor.ExtractWordsAsync(pdf, 0)).Single(w => w.Text == "heading");
        Assert.Equal(0xFFFFFFFFu, before.ColorArgb);
        Assert.True(before.IsBold);

        var result = await ReplaceAllAsync(pdf, "heading", "title");

        var after = (await _extractor.ExtractWordsAsync(result, 0)).Single(w => w.Text == "title");
        Assert.Equal(0xFFFFFFFFu, after.ColorArgb); // white on the dark band, not black
        Assert.True(after.IsBold);
    }

    [Fact]
    public async Task ApplyReplacements_TextInASharedForm_ChangesOnlyThatPage()
    {
        // A running header drawn from one form XObject on every page, as layout tools do.
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        byte[] pdf;
        using (var document = new PdfDocument())
        {
            var header = new XForm(document, XUnit.FromPoint(400), XUnit.FromPoint(40));
            using (var fg = XGraphics.FromForm(header))
                fg.DrawString("Annual report draft", new XFont("Arial", 14), XBrushes.Black, new XPoint(10, 25));
            for (var i = 0; i < 2; i++)
            {
                var page = document.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(header, 50, 60);
            }
            using var ms = new MemoryStream();
            document.Save(ms);
            pdf = ms.ToArray();
        }

        var edits = await _extractor.PlanReplacementsAsync(pdf, 0, "draft", "final");
        var result = await _sut.ApplyReplacementsAsync(pdf, edits);

        var page1 = await _extractor.ExtractTextAsync(result, 0);
        var page2 = await _extractor.ExtractTextAsync(result, 1);
        Assert.Contains("final", page1);
        Assert.DoesNotContain("draft", page1); // removed from the form's copy, not painted over
        Assert.Contains("draft", page2);        // the shared form itself is untouched
        Assert.DoesNotContain("final", page2);
    }

    [Fact]
    public async Task ApplyReplacements_NoEdits_ReturnsInput()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Hello");
        Assert.Same(pdf, await _sut.ApplyReplacementsAsync(pdf, []));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsValidPdf(byte[] bytes) =>
        bytes.Length > 4 &&
        bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    private static int PageCount(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }
}
