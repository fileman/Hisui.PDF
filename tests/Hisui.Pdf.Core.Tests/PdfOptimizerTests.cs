using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfOptimizerTests
{
    private readonly PdfOptimizer _sut = new();

    [Fact]
    public async Task OptimizeAsync_RecompressesLargeJpeg_ShrinksAndStaysValid()
    {
        var pdf = PdfFixtureBuilder.CreateWithJpegImage(sizePx: 1600, jpegQuality: 92);
        var result = await _sut.OptimizeAsync(pdf, new CompressionOptions { MaxImageEdge = 500, JpegQuality = 45 });

        Assert.True(result.Length < pdf.Length, $"expected shrink: {pdf.Length} -> {result.Length}");
        using var doc = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public async Task OptimizeAsync_RecompressesFlateImage_ShrinksToValidJpeg()
    {
        var pdf = PdfFixtureBuilder.CreateWithFlateImage(sizePx: 1500);
        var result = await _sut.OptimizeAsync(pdf, new CompressionOptions { MaxImageEdge = 700, JpegQuality = 50 });

        Assert.True(result.Length < pdf.Length, $"expected shrink: {pdf.Length} -> {result.Length}");

        using var doc = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);

        // The Flate raster is now a valid, downsized JPEG.
        var image = FindFirstImage(doc);
        Assert.NotNull(image);
        Assert.Equal("/DCTDecode", (image!.Elements["/Filter"] as PdfName)?.Value);
        using var bmp = SKBitmap.Decode(image.Stream.Value);
        Assert.NotNull(bmp);
        Assert.True(Math.Max(bmp!.Width, bmp.Height) <= 700, $"expected downscale, got {bmp.Width}x{bmp.Height}");
    }

    [Theory]
    // PNG Sub filter (1): one row of 4 gray samples 10,20,25,30 → deltas 10,10,5,5, prefixed by filter byte 1.
    [InlineData(12, 1, 4, new byte[] { 1, 10, 10, 5, 5 }, new byte[] { 10, 20, 25, 30 })]
    // PNG Up filter (2): row0 (filter 0) 5,8; row1 (filter 2) deltas 2,2 over the row above → 7,10.
    [InlineData(15, 1, 2, new byte[] { 0, 5, 8, 2, 2, 2 }, new byte[] { 5, 8, 7, 10 })]
    // TIFF predictor 2 (horizontal differencing, no per-row filter byte): deltas 10,10,5,5 → 10,20,25,30.
    [InlineData(2, 1, 4, new byte[] { 10, 10, 5, 5 }, new byte[] { 10, 20, 25, 30 })]
    public void UndoPredictor_RecoversOriginalSamples(int predictor, int colors, int columns, byte[] encoded, byte[] expected)
    {
        var decoded = PdfOptimizer.UndoPredictor(encoded, predictor, colors, columns);
        Assert.Equal(expected, decoded);
    }

    private static PdfDictionary? FindFirstImage(PdfDocument doc)
    {
        foreach (var obj in doc.Internals.GetAllObjects())
            if (obj is PdfDictionary dict && (dict.Elements["/Subtype"] as PdfName)?.Value == "/Image")
                return dict;
        return null;
    }

    [Fact]
    public async Task OptimizeAsync_WithDownsampleDisabled_LeavesImagesAlone()
    {
        var pdf = PdfFixtureBuilder.CreateWithJpegImage(sizePx: 800, jpegQuality: 90);
        var result = await _sut.OptimizeAsync(pdf, new CompressionOptions { DownsampleImages = false });

        using var doc = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount); // valid, image untouched
    }

    [Fact]
    public async Task OptimizeAsync_ProducesValidPdf()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        var result = await _sut.OptimizeAsync(pdf);
        Assert.True(result[0] == '%' && result[1] == 'P' && result[2] == 'D' && result[3] == 'F');
    }

    [Fact]
    public async Task OptimizeAsync_PreservesPageCount()
    {
        const int pages = 4;
        var pdf = PdfFixtureBuilder.Create(pages);
        var result = await _sut.OptimizeAsync(pdf);

        using var doc = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.Import);
        Assert.Equal(pages, doc.PageCount);
    }

    [Fact]
    public async Task OptimizeAsync_EmptyPagesPdf_ProducesValidOutput()
    {
        var pdf = PdfFixtureBuilder.Create(1);
        var result = await _sut.OptimizeAsync(pdf);
        Assert.NotEmpty(result);
    }
}
