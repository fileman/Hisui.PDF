using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfAnnotationServiceTests
{
    private readonly PdfAnnotationService _sut = new();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsValidPdf(byte[] bytes) =>
        bytes.Length > 4 &&
        bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F';

    private static int PageCount(byte[] pdf)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    // ── watermark ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddTextWatermark_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var result = await _sut.AddTextWatermarkAsync(pdf, "CONFIDENTIAL");
        Assert.True(IsValidPdf(result));
        Assert.Equal(2, PageCount(result));
    }

    [Fact]
    public async Task AddTextWatermark_WithCustomOptions_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var opts = new WatermarkOptions { Opacity = 0.5, RotationDegrees = 30, ColorHex = "#FF0000" };
        var result = await _sut.AddTextWatermarkAsync(pdf, "DRAFT", opts);
        Assert.True(IsValidPdf(result));
    }

    // ── highlight ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddHighlight_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var rect = new PdfRect(0.1, 0.2, 0.8, 0.3);
        var result = await _sut.AddHighlightAsync(pdf, 0, rect);
        Assert.True(IsValidPdf(result));
        Assert.Equal(1, PageCount(result));
    }

    [Fact]
    public async Task AddHighlight_OutOfRangePage_Throws()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _sut.AddHighlightAsync(pdf, 5, new PdfRect(0, 0, 1, 1)));
    }

    // ── rectangle ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddRectangle_StrokeOnly_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.AddRectangleAsync(pdf, 0, new PdfRect(0.05, 0.05, 0.95, 0.95));
        Assert.True(IsValidPdf(result));
    }

    [Fact]
    public async Task AddRectangle_WithFill_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.AddRectangleAsync(pdf, 0, new PdfRect(0.2, 0.2, 0.6, 0.4), "#0000FF", "#AADDFF");
        Assert.True(IsValidPdf(result));
    }

    // ── free text ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddFreeText_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.AddFreeTextAsync(pdf, 0, new PdfRect(0.1, 0.1, 0.5, 0.2), "Hello PDF");
        Assert.True(IsValidPdf(result));
    }

    [Fact]
    public async Task AddFreeText_WithZeroAreaRect_DoesNotThrow()
    {
        // A click (or sub-padding drag) yields a tiny/zero box; the padded inner rect must not go negative.
        var pdf = PdfFixtureBuilder.Create(1);
        var degenerate = new PdfRect(0.5, 0.5, 0.5, 0.5); // width = height = 0
        var result = await _sut.AddFreeTextAsync(pdf, 0, degenerate, "x");
        Assert.True(IsValidPdf(result));
    }

    [Fact]
    public async Task AddFreeText_WithSubPaddingRect_DoesNotThrow()
    {
        // ~3pt box on an A4 page (< the 6pt inner padding) — previously threw WidthAndHeightCannotBeNegative.
        var pdf = PdfFixtureBuilder.Create(1);
        var tiny = new PdfRect(0.5, 0.5, 0.505, 0.504);
        var result = await _sut.AddFreeTextAsync(pdf, 0, tiny, "x");
        Assert.True(IsValidPdf(result));
    }

    // ── sticky note ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AddStickyNote_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.AddStickyNoteAsync(pdf, 0, 0.5, 0.5, "Review this section");
        Assert.True(IsValidPdf(result));
    }

    // ── chaining ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChainMultipleAnnotations_PreservesPageCount()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        var step1 = await _sut.AddTextWatermarkAsync(pdf, "DRAFT");
        var step2 = await _sut.AddRectangleAsync(step1, 1, new PdfRect(0.1, 0.1, 0.9, 0.2));
        var step3 = await _sut.AddHighlightAsync(step2, 2, new PdfRect(0.2, 0.3, 0.7, 0.4));
        Assert.Equal(3, PageCount(step3));
    }

    // ── editable watermark ───────────────────────────────────────────────────

    /// <summary>The page's content streams joined, as a viewer reads them.</summary>
    private static string PageContent(byte[] pdf, int page = 0)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return string.Join("\n", doc.Pages[page].Contents.Elements
            .Select(e => (PdfSharp.Pdf.PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)e).Value)
            .Select(d => System.Text.Encoding.Latin1.GetString(d.Stream.UnfilteredValue)));
    }

    private static int Balance(string content) =>
        System.Text.RegularExpressions.Regex.Count(content, @"(?<!\S)q(?!\S)") -
        System.Text.RegularExpressions.Regex.Count(content, @"(?<!\S)Q(?!\S)");

    [Fact]
    public async Task Watermark_IsReplaced_ReadBack_AndRemoved()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Contratto", 2);
        Assert.Null(await _sut.ReadWatermarkAsync(pdf));

        var first = await _sut.AddTextWatermarkAsync(pdf, "BOZZA");
        // A later mark-up wraps the earlier streams in q/Q: removing the watermark must keep the nesting balanced.
        var marked = await _sut.AddHighlightAsync(first, 0, new PdfRect(0.1, 0.1, 0.3, 0.2));
        var second = await _sut.AddTextWatermarkAsync(marked, "COPIA è", new WatermarkOptions { Opacity = 0.5, FontSizePoints = 40, RotationDegrees = 30, ColorHex = "#FF0000" });

        var info = await _sut.ReadWatermarkAsync(second);
        Assert.NotNull(info);
        Assert.Equal("COPIA è", info.Text);
        Assert.Equal(0.5, info.Options.Opacity, 3);
        Assert.Equal(40, info.Options.FontSizePoints, 3);
        Assert.Equal(30, info.Options.RotationDegrees, 3);
        Assert.Equal("#FF0000", info.Options.ColorHex);
        for (var p = 0; p < 2; p++)
        {
            var content = PageContent(second, p);
            Assert.DoesNotContain("(BOZZA)", content); // the old one is gone, not drawn underneath
            Assert.Equal(0, Balance(content));
        }

        var removed = await _sut.RemoveWatermarkAsync(second);
        Assert.Null(await _sut.ReadWatermarkAsync(removed));
        var left = PageContent(removed);
        Assert.Equal(0, Balance(left));
        Assert.Contains("Contratto", left);
        Assert.Contains(" re", left); // the highlight stays
    }
}
