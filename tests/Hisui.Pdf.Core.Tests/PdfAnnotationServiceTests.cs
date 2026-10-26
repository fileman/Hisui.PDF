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

    [Fact]
    public async Task AddFreeText_WithCharPitch_PlacesEachCharacterInItsOwnCell()
    {
        // Boxed-form mode (e.g. IBAN): 10 cells of 20pt starting 3pt inside the rect; the glyphs must be read back
        // in order, each inside its cell on an A4 page (595pt wide).
        var pdf = PdfFixtureBuilder.Create(1);
        var rect = new PdfRect(0.1, 0.5, 0.9, 0.55);
        var result = await _sut.AddFreeTextAsync(pdf, 0, rect, "IT60X05428", charPitchPoints: 20, background: false);
        Assert.True(IsValidPdf(result));

        var letters = (await new PdfTextExtractor().ExtractWordsAsync(result, 0))
            .Where(w => w.BoundingBox.Top >= 0.49)
            .OrderBy(w => w.BoundingBox.Left).ToList();
        Assert.Equal("IT60X05428", string.Concat(letters.Select(w => w.Text)));
        var cellStart = (0.1 * 595 + 3) / 595;
        var cellW = 20.0 / 595;
        for (var i = 0; i < letters.Count; i++)
        {
            Assert.InRange(letters[i].BoundingBox.Left, cellStart + i * cellW, cellStart + (i + 1) * cellW);
            Assert.InRange(letters[i].BoundingBox.Right, cellStart + i * cellW, cellStart + (i + 1) * cellW);
        }
    }

    [Fact]
    public async Task AddFreeText_MultiLine_DrawsOneLinePerNewline()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.AddFreeTextAsync(pdf, 0, new PdfRect(0.1, 0.5, 0.9, 0.7), "first\nsecond", background: false);
        var words = (await new PdfTextExtractor().ExtractWordsAsync(result, 0)).Where(w => w.BoundingBox.Top >= 0.49).ToList();
        var first = Assert.Single(words, w => w.Text == "first");
        var second = Assert.Single(words, w => w.Text == "second");
        Assert.True(second.BoundingBox.Top > first.BoundingBox.Bottom - 0.001, "second line must sit below the first");
        Assert.InRange(Math.Abs(first.BoundingBox.Left - second.BoundingBox.Left), 0, 0.005); // same left edge (glyph bearings aside)
    }

    [Fact]
    public async Task AddFreeText_CanBeReadBackAndRemoved()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var rect = new PdfRect(0.2, 0.3, 0.6, 0.35);
        var withText = await _sut.AddFreeTextAsync(pdf, 0, rect, "Hello", "#FF0000", 14, charPitchPoints: 12, background: false);

        var info = Assert.Single(await _sut.ReadPlacedAsync(withText, 0));
        Assert.Equal(AnnotationTool.FreeText, info.Tool);
        Assert.Equal("Hello", info.Text);
        Assert.Equal(rect, info.Rect);
        Assert.Equal(14, info.FontSizePoints);
        Assert.Equal("#FF0000", info.ColorHex);
        Assert.Equal(12, info.CharPitchPoints);
        Assert.False(info.Background);
        Assert.Contains("Hello", (await new PdfTextExtractor().ExtractTextAsync(withText)).Replace(" ", "")); // cells read back as spaced letters

        // Redrawn with the same id (an edit or move): still one text, same identity.
        var redrawn = await _sut.AddFreeTextAsync(await _sut.RemovePlacedAsync(withText, 0, info.Id), 0, rect, "Hello again", id: info.Id);
        Assert.Equal(info.Id, Assert.Single(await _sut.ReadPlacedAsync(redrawn, 0)).Id);

        var removed = await _sut.RemovePlacedAsync(withText, 0, info.Id);
        Assert.True(IsValidPdf(removed));
        Assert.Empty(await _sut.ReadPlacedAsync(removed, 0));
        Assert.DoesNotContain("Hello", (await new PdfTextExtractor().ExtractTextAsync(removed)).Replace(" ", ""));
    }

    [Fact]
    public async Task RemoveFreeText_LeavesOtherFreeTextsAndTheWatermark()
    {
        var pdf = await _sut.AddTextWatermarkAsync(PdfFixtureBuilder.Create(1), "DRAFT");
        pdf = await _sut.AddFreeTextAsync(pdf, 0, new PdfRect(0.1, 0.1, 0.5, 0.15), "one");
        pdf = await _sut.AddFreeTextAsync(pdf, 0, new PdfRect(0.1, 0.2, 0.5, 0.25), "two");
        var texts = await _sut.ReadPlacedAsync(pdf, 0);
        Assert.Equal(["one", "two"], texts.Select(t => t.Text));

        var result = await _sut.RemovePlacedAsync(pdf, 0, texts[0].Id);
        Assert.Equal("two", Assert.Single(await _sut.ReadPlacedAsync(result, 0)).Text);
        Assert.NotNull(await _sut.ReadWatermarkAsync(result));
    }

    // ── placed annotations (tagged, read back, removed) ──────────────────────

    // A minimal valid 1×1 opaque PNG (white pixel).
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task ReadPlaced_ReturnsHighlightRectangleAndImage_WithTheirSettings()
    {
        var hl = new PdfRect(0.1, 0.1, 0.5, 0.15);
        var rc = new PdfRect(0.2, 0.2, 0.6, 0.4);
        var im = new PdfRect(0.3, 0.5, 0.4, 0.6);
        var pdf = await _sut.AddHighlightAsync(PdfFixtureBuilder.Create(1), 0, hl, "#00FF00", 0.25);
        pdf = await _sut.AddRectangleAsync(pdf, 0, rc, "#0000FF", "#AADDFF", 3.5);
        pdf = await _sut.AddImageOverlayAsync(pdf, 0, OnePixelPng, im);

        var placed = await _sut.ReadPlacedAsync(pdf, 0);
        Assert.Equal([AnnotationTool.Highlight, AnnotationTool.Rectangle, AnnotationTool.ImageOverlay], placed.Select(a => a.Tool));
        Assert.Equal(3, placed.Select(a => a.Id).Distinct().Count());

        Assert.Equal(hl, placed[0].Rect);
        Assert.Equal("#00FF00", placed[0].ColorHex);
        Assert.Equal(0.25, placed[0].Opacity, 3);

        Assert.Equal(rc, placed[1].Rect);
        Assert.Equal("#0000FF", placed[1].ColorHex);
        Assert.Equal("#AADDFF", placed[1].FillHex);
        Assert.Equal(3.5, placed[1].LineWidth, 3);

        Assert.Equal(im, placed[2].Rect);
        Assert.Equal(OnePixelPng, placed[2].ImageBytes);

        // An unfilled rectangle reads back with no fill.
        var unfilled = await _sut.AddRectangleAsync(PdfFixtureBuilder.Create(1), 0, rc);
        Assert.Null(Assert.Single(await _sut.ReadPlacedAsync(unfilled, 0)).FillHex);

        // ToInputModel carries everything needed to redraw it.
        var model = placed[1].ToInputModel();
        Assert.Equal(AnnotationTool.Rectangle, model.Tool);
        Assert.Equal(rc, model.Rect);
        Assert.Equal("#AADDFF", model.FillHex);
        Assert.Equal(3.5, model.LineWidth, 3);
        Assert.Equal(OnePixelPng, placed[2].ToInputModel().ImageBytes);
    }

    [Fact]
    public async Task RemovePlaced_RemovesExactlyThatOne_LeavesOthersAndWatermark()
    {
        var pdf = await _sut.AddTextWatermarkAsync(PdfFixtureBuilder.Create(1), "DRAFT");
        pdf = await _sut.AddHighlightAsync(pdf, 0, new PdfRect(0.1, 0.1, 0.5, 0.15));
        pdf = await _sut.AddRectangleAsync(pdf, 0, new PdfRect(0.2, 0.2, 0.6, 0.4));
        pdf = await _sut.AddFreeTextAsync(pdf, 0, new PdfRect(0.1, 0.5, 0.5, 0.55), "keep me");
        pdf = await _sut.AddImageOverlayAsync(pdf, 0, OnePixelPng, new PdfRect(0.3, 0.6, 0.4, 0.7));
        var placed = await _sut.ReadPlacedAsync(pdf, 0);
        Assert.Equal(4, placed.Count);

        var result = await _sut.RemovePlacedAsync(pdf, 0, placed[1].Id);
        Assert.True(IsValidPdf(result));
        var left = await _sut.ReadPlacedAsync(result, 0);
        Assert.Equal([placed[0].Id, placed[2].Id, placed[3].Id], left.Select(a => a.Id));
        Assert.Equal([AnnotationTool.Highlight, AnnotationTool.FreeText, AnnotationTool.ImageOverlay], left.Select(a => a.Tool));
        Assert.NotNull(await _sut.ReadWatermarkAsync(result));
        Assert.Equal(0, Balance(PageContent(result)));
        Assert.Contains("keep", (await new PdfTextExtractor().ExtractTextAsync(result)));

        // A move: remove, redraw elsewhere with the same id.
        var target = new PdfRect(0.5, 0.5, 0.9, 0.55);
        var moved = await _sut.AddHighlightAsync(await _sut.RemovePlacedAsync(result, 0, placed[0].Id), 0, target, id: placed[0].Id);
        var afterMove = await _sut.ReadPlacedAsync(moved, 0);
        Assert.Equal(3, afterMove.Count);
        Assert.Equal(target, Assert.Single(afterMove, a => a.Id == placed[0].Id).Rect);
    }

    [Fact]
    public async Task AddFreeText_BoldItalic_ReadsBackStyleAndStaysExtractable()
    {
        var pdf = await _sut.AddFreeTextAsync(PdfFixtureBuilder.Create(1), 0, new PdfRect(0.1, 0.5, 0.9, 0.55), "Styled", background: false, bold: true, italic: true);
        var placed = Assert.Single(await _sut.ReadPlacedAsync(pdf, 0));
        Assert.Equal(AnnotationTool.FreeText, placed.Tool);
        Assert.True(placed.Bold);
        Assert.True(placed.Italic);
        Assert.Equal("Styled", placed.Text);
        Assert.Contains("Styled", (await new PdfTextExtractor().ExtractTextAsync(pdf)).Replace(" ", ""));

        var model = placed.ToInputModel();
        Assert.True(model.Bold);
        Assert.True(model.Italic);

        // Plain text reads back unstyled.
        var plain = await _sut.AddFreeTextAsync(PdfFixtureBuilder.Create(1), 0, new PdfRect(0.1, 0.5, 0.9, 0.55), "Plain");
        var plainPlaced = Assert.Single(await _sut.ReadPlacedAsync(plain, 0));
        Assert.False(plainPlaced.Bold);
        Assert.False(plainPlaced.Italic);
        Assert.Equal("Plain", plainPlaced.Text);
    }

    [Fact]
    public async Task FreeText_TaggedWithTheOldKey_IsStillReadAndRemoved()
    {
        // A PDF saved by the previous build: the free text's stream carries /HisuiFreeText (no /Tool, /Bold, /Italic).
        var rect = new PdfRect(0.2, 0.3, 0.6, 0.35);
        var pdf = await _sut.AddFreeTextAsync(PdfFixtureBuilder.Create(1), 0, rect, "Legacy", "#FF0000", 14, charPitchPoints: 12, background: false);
        byte[] legacy;
        using (var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify))
        {
            var content = (PdfSharp.Pdf.PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)doc.Pages[0].Contents.Elements[^1]).Value;
            var tag = content.Elements.GetDictionary("/HisuiAnnot")!;
            foreach (var k in new[] { "/Tool", "/Bold", "/Italic" }) tag.Elements.Remove(k);
            content.Elements.Remove("/HisuiAnnot");
            content.Elements["/HisuiFreeText"] = tag;
            using var ms = new MemoryStream();
            doc.Save(ms);
            legacy = ms.ToArray();
        }

        var placed = Assert.Single(await _sut.ReadPlacedAsync(legacy, 0));
        Assert.Equal(AnnotationTool.FreeText, placed.Tool);
        Assert.Equal("Legacy", placed.Text);
        Assert.Equal(rect, placed.Rect);
        Assert.Equal(14, placed.FontSizePoints);
        Assert.Equal("#FF0000", placed.ColorHex);
        Assert.Equal(12, placed.CharPitchPoints);
        Assert.False(placed.Background);

        var removed = await _sut.RemovePlacedAsync(legacy, 0, placed.Id);
        Assert.Empty(await _sut.ReadPlacedAsync(removed, 0));
        Assert.DoesNotContain("Legacy", (await new PdfTextExtractor().ExtractTextAsync(removed)).Replace(" ", ""));
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
