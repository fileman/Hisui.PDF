using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf;
using SkiaSharp;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfRendererTests : IDisposable
{
    private readonly PdfiumRenderer _sut = new();

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task GetPageCount_MatchesDocument()
    {
        var pdf = PdfFixtureBuilder.Create(3);
        Assert.Equal(3, await _sut.GetPageCountAsync(pdf));
    }

    [Fact]
    public async Task RenderPageToPng_ReturnsPngBytes()
    {
        var pdf = PdfFixtureBuilder.Create(2);

        var png = await _sut.RenderPageToPngAsync(pdf, pageIndex: 1, dpi: 96);

        Assert.NotNull(png);
        Assert.True(png.Length > 0);
        // PNG signature: 89 50 4E 47.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }

    [Theory]
    [InlineData(72)]
    [InlineData(40)]
    [InlineData(96)]
    [InlineData(150)]
    public async Task RenderPage_RawDimensionsMatchPng(int dpi)
    {
        // A4 is 595.276 x 841.89 pt, so most DPIs produce fractional pixel sizes (rounding must agree).
        var pdf = PdfFixtureBuilder.Create(1);

        var raw = await _sut.RenderPageAsync(pdf, 0, dpi);
        using var decoded = SKBitmap.Decode(await _sut.RenderPageToPngAsync(pdf, 0, dpi));

        Assert.Equal(decoded.Width, raw.Width);
        Assert.Equal(decoded.Height, raw.Height);
        Assert.Equal(raw.Width * 4, raw.Stride);
        Assert.Equal(raw.Stride * raw.Height, raw.Bgra.Length);
    }

    [Fact]
    public async Task RenderPage_RotatedPage_DimensionsMatchPngAndAreSwapped()
    {
        var upright = PdfFixtureBuilder.Create(1);
        var rotated = RotateFirstPage(upright, 90);

        var a = await _sut.RenderPageAsync(upright, 0, 100);
        var b = await _sut.RenderPageAsync(rotated, 0, 100);
        using var png = SKBitmap.Decode(await _sut.RenderPageToPngAsync(rotated, 0, 100));

        Assert.Equal(a.Width, b.Height);
        Assert.Equal(a.Height, b.Width);
        Assert.Equal(png.Width, b.Width);
        Assert.Equal(png.Height, b.Height);
    }

    [Fact]
    public async Task RenderPage_IsOpaqueWhiteBackgroundWithContent()
    {
        var raw = await _sut.RenderPageAsync(PdfFixtureBuilder.Create(1), 0, 72);

        Assert.All(Enumerable.Range(0, raw.Width * raw.Height), i => Assert.Equal(255, raw.Bgra[(i * 4) + 3]));
        // Top-left corner is blank page (white); the steel-blue bar is somewhere inside.
        Assert.Equal([255, 255, 255], raw.Bgra[..3]);
        Assert.Contains(Enumerable.Range(0, raw.Width * raw.Height), i => raw.Bgra[i * 4] != 255);
    }

    [Fact]
    public async Task RenderPage_MatchesPngPixels()
    {
        var pdf = PdfFixtureBuilder.Create(2);

        var raw = await _sut.RenderPageAsync(pdf, 1, 72);
        using var decoded = SKBitmap.Decode(await _sut.RenderPageToPngAsync(pdf, 1, 72));

        Assert.Equal(decoded.GetPixelSpan().ToArray(), raw.Bgra);
    }

    [Fact]
    public async Task SameBuffer_ReusesOpenDocument()
    {
        var pdf = PdfFixtureBuilder.Create(3);

        await _sut.GetPageCountAsync(pdf);
        await _sut.RenderPageAsync(pdf, 0, 40);
        await _sut.RenderPageAsync(pdf, 2, 40);
        Assert.Equal(1, _sut.OpenDocumentCount);

        // An equal-content copy is a different buffer, hence a different cache entry.
        await _sut.RenderPageAsync((byte[])pdf.Clone(), 0, 40);
        Assert.Equal(2, _sut.OpenDocumentCount);
    }

    [Fact]
    public async Task Release_ClosesDocument_AndRenderingStillWorksAfterwards()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        await _sut.RenderPageAsync(pdf, 0, 40);

        _sut.Release(pdf);
        Assert.Equal(0, _sut.OpenDocumentCount);

        Assert.Equal(2, await _sut.GetPageCountAsync(pdf));
    }

    [Fact]
    public async Task Cache_EvictsLeastRecentlyUsed_BeyondTheLimit()
    {
        var docs = Enumerable.Range(0, PdfiumRenderer.MaxOpenDocuments + 3)
            .Select(i => PdfFixtureBuilder.Create(i + 1)).ToArray();

        for (var i = 0; i < docs.Length; i++)
            Assert.Equal(i + 1, await _sut.GetPageCountAsync(docs[i]));

        Assert.Equal(PdfiumRenderer.MaxOpenDocuments, _sut.OpenDocumentCount);
        // An evicted document transparently reopens.
        Assert.Equal(1, await _sut.GetPageCountAsync(docs[0]));
    }

    [Fact]
    public async Task DifferentBuffers_RenderIndependently()
    {
        var a = PdfFixtureBuilder.Create(2);
        var b = RotateFirstPage(PdfFixtureBuilder.Create(5), 90);

        var ra = await _sut.RenderPageAsync(a, 0, 50);
        var rb = await _sut.RenderPageAsync(b, 0, 50);
        var ra2 = await _sut.RenderPageAsync(a, 0, 50);

        Assert.Equal(2, await _sut.GetPageCountAsync(a));
        Assert.Equal(5, await _sut.GetPageCountAsync(b));
        Assert.NotEqual(ra.Width, rb.Width);
        Assert.Equal(ra.Bgra, ra2.Bgra);
    }

    [Fact]
    public async Task ConcurrentRenders_DoNotCrashAndAreDeterministic()
    {
        var docs = new[] { PdfFixtureBuilder.Create(6), PdfFixtureBuilder.Create(4), PdfFixtureBuilder.Create(5) };
        var expected = await _sut.RenderPageAsync(docs[0], 3, 60);

        var tasks = Enumerable.Range(0, 96).Select(async i =>
        {
            var doc = docs[i % docs.Length];
            var page = await _sut.RenderPageAsync(doc, i % 4, 60);
            _ = await _sut.GetPageCountAsync(doc);
            if (i % 8 == 0) _ = await _sut.RenderPageToPngAsync(doc, i % 4, 40);
            return (i, page);
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        foreach (var (i, page) in results.Where(r => r.i % docs.Length == 0 && r.i % 4 == 3))
            Assert.Equal(expected.Bgra, page.Bgra);
    }

    [Fact]
    public async Task RenderPage_OutOfRangeIndex_Throws()
    {
        var pdf = PdfFixtureBuilder.Create(2);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _sut.RenderPageAsync(pdf, 2, 72));
    }

    [Fact]
    public async Task RenderPage_NotAPdf_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RenderPageAsync([1, 2, 3, 4], 0, 72));
    }

    [Fact]
    public async Task RenderPage_AfterDispose_Throws()
    {
        var sut = new PdfiumRenderer();
        var pdf = PdfFixtureBuilder.Create(1);
        await sut.RenderPageAsync(pdf, 0, 40);
        sut.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sut.RenderPageAsync(pdf, 0, 40));
    }

    private static byte[] RotateFirstPage(byte[] pdf, int degrees)
    {
        using var input = new MemoryStream(pdf);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        doc.Pages[0].Rotate = degrees;
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }
}
