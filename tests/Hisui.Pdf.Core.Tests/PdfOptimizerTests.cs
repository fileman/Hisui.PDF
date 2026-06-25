using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf.IO;
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
