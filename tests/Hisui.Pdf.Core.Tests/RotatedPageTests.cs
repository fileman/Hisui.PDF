using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
using PdfSharp.Fonts;
using SkiaSharp;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

/// <summary>Content added through a normalised rendered-orientation rect must land there on pages with /Rotate.</summary>
public class RotatedPageTests
{
    private static byte[] WithRotate(int degrees)
    {
        using var doc = PdfReader.Open(new MemoryStream(PdfFixtureBuilder.Create(1)), PdfDocumentOpenMode.Modify);
        doc.Pages[0].Rotate = degrees;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static async Task<SKBitmap> Render(byte[] pdf) =>
        SKBitmap.Decode(await new PdfiumRenderer().RenderPageToPngAsync(pdf, 0, 72));

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 70 && c.Blue < 70;
    private static bool IsFormBlue(SKColor c) => c.Blue > 240 && c.Red is > 220 and < 250; // 0.93/0.95/1 field background

    private static SKColor At(SKBitmap bmp, PdfRect r) =>
        bmp.GetPixel((int)((r.Left + r.Right) / 2 * bmp.Width), (int)((r.Top + r.Bottom) / 2 * bmp.Height));

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task Rectangle_LandsAtRenderedPosition(int rotate)
    {
        var rect = new PdfRect(0.55, 0.70, 0.85, 0.80);
        var pdf = await new PdfAnnotationService().AddRectangleAsync(WithRotate(rotate), 0, rect, "#FF0000", "#FF0000");

        using var bmp = await Render(pdf);
        Assert.True(IsRed(At(bmp, rect)), $"rotate {rotate}: expected red at rect centre");
        Assert.False(IsRed(At(bmp, new PdfRect(0.1, 0.1, 0.2, 0.2))));
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task CheckBoxField_LandsAtRenderedPosition(int rotate)
    {
        var rect = new PdfRect(0.55, 0.70, 0.85, 0.80);
        var pdf = await new PdfFormService(new PdfiumRenderer())
            .AddFieldAsync(WithRotate(rotate), 0, rect, PdfFormFieldType.Signature, "S");

        using var bmp = await Render(pdf);
        Assert.True(IsFormBlue(At(bmp, rect)), $"rotate {rotate}: expected field background at rect centre, got {At(bmp, rect)}");
        Assert.False(IsFormBlue(At(bmp, new PdfRect(0.1, 0.1, 0.2, 0.2))));
    }

    private static byte[] TextWithRotate(int degrees)
    {
        using var doc = PdfReader.Open(new MemoryStream(PdfFixtureBuilder.CreateWithText("Hello world")), PdfDocumentOpenMode.Modify);
        doc.Pages[0].Rotate = degrees;
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Bounding box (normalised) of every non-white pixel.</summary>
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

    private static void AssertClose(PdfRect expected, PdfRect actual, double tol = 0.02)
    {
        Assert.InRange(actual.Left, expected.Left - tol, expected.Left + tol);
        Assert.InRange(actual.Top, expected.Top - tol, expected.Top + tol);
        Assert.InRange(actual.Right, expected.Right - tol, expected.Right + tol);
        Assert.InRange(actual.Bottom, expected.Bottom - tol, expected.Bottom + tol);
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task ExtractedWords_AreInRenderedOrientation(int rotate)
    {
        var pdf = TextWithRotate(rotate);
        var words = await new PdfTextExtractor().ExtractWordsAsync(pdf, 0);

        var union = new PdfRect(words.Min(w => w.BoundingBox.Left), words.Min(w => w.BoundingBox.Top),
                                words.Max(w => w.BoundingBox.Right), words.Max(w => w.BoundingBox.Bottom));
        using var bmp = await Render(pdf);
        AssertClose(InkBox(bmp), union);
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task TextEdit_ReplacesWordInPlace(int rotate)
    {
        var pdf = TextWithRotate(rotate);
        var ws = await new PdfTextExtractor().ExtractWordsAsync(pdf, 0);
        var word = ws.FirstOrDefault(w => w.Text == "world") ?? throw new Exception("words: " + string.Join("|", ws.Select(w => w.Text)));

        var edited = await new PdfTextEditService().ReplaceWordAsync(pdf, 0, word.BoundingBox, "WORLD", word.FontSizePoints);
        // The old text stays in the stream under the cover box, so judge by pixels: the page's ink extent
        // must stay put (a misplaced or mis-rotated replacement would stretch it), and the word box has ink.
        using var before = await Render(pdf);
        using var after = await Render(edited);
        var inkBefore = InkBox(before);
        var inkAfter = InkBox(after);
        Assert.InRange(inkAfter.Left, inkBefore.Left - 0.02, inkBefore.Left + 0.02);
        Assert.InRange(inkAfter.Top, inkBefore.Top - 0.02, inkBefore.Top + 0.02);
        Assert.InRange(inkAfter.Right, inkBefore.Right - 0.05, inkBefore.Right + 0.05);
        Assert.InRange(inkAfter.Bottom, inkBefore.Bottom - 0.02, inkBefore.Bottom + 0.02);
        Assert.NotEqual(before.Bytes.Length == after.Bytes.Length && before.Bytes.SequenceEqual(after.Bytes), true);
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task Search_FindsPhraseWithBoxInsideRenderedInk(int rotate)
    {
        var pdf = TextWithRotate(rotate);
        var hit = Assert.Single(await new PdfTextExtractor().SearchAsync(pdf, "hello world"));

        using var bmp = await Render(pdf);
        var ink = InkBox(bmp);
        foreach (var b in hit.Boxes)
        {
            Assert.InRange(b.Left, ink.Left - 0.02, ink.Right);
            Assert.InRange(b.Right, ink.Left, ink.Right + 0.02);
            Assert.InRange(b.Top, ink.Top - 0.02, ink.Bottom);
            Assert.InRange(b.Bottom, ink.Top, ink.Bottom + 0.02);
        }
    }

    [Fact]
    public void RotateClockwise_RoundTripsAndMatchesQuarterTurns()
    {
        var r = new PdfRect(0.1, 0.2, 0.4, 0.3);
        Assert.Equal(new PdfRect(0.7, 0.1, 0.8, 0.4).Left, r.RotateClockwise(90).Left, 9);
        Assert.Equal(new PdfRect(0.7, 0.1, 0.8, 0.4).Bottom, r.RotateClockwise(90).Bottom, 9);
        foreach (var deg in new[] { 90, 180, 270 })
        {
            var back = r.RotateClockwise(deg).RotateClockwise(360 - deg);
            Assert.Equal(r.Left, back.Left, 9); Assert.Equal(r.Top, back.Top, 9);
            Assert.Equal(r.Right, back.Right, 9); Assert.Equal(r.Bottom, back.Bottom, 9);
        }
    }
}
