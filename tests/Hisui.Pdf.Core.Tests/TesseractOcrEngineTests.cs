using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class TesseractOcrEngineTests
{
    private readonly TesseractOcrEngine _sut = new();

    private static readonly string? BundledTessData = FindBundledTessData();

    private static string? FindBundledTessData()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Hisui.Pdf.App", "tessdata");
            if (File.Exists(Path.Combine(candidate, "eng.traineddata"))) return candidate;
        }
        return null;
    }

    [Fact]
    public async Task Recognize_WithMissingTessDataDirectory_ThrowsOcrUnavailable()
    {
        var options = new OcrOptions { TessDataPath = Path.Combine(Path.GetTempPath(), "hisui-no-such-tessdata-" + Guid.NewGuid().ToString("N")) };

        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(
            () => _sut.RecognizeAsync([1, 2, 3], 0, options));
        Assert.Contains("language data", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recognize_WithMissingLanguageFile_ThrowsOcrUnavailable()
    {
        // A real directory but without the requested traineddata file.
        var dir = Directory.CreateTempSubdirectory("hisui-tessdata-").FullName;
        try
        {
            var options = new OcrOptions { TessDataPath = dir, Language = "zzz" };

            var ex = await Assert.ThrowsAsync<OcrUnavailableException>(
                () => _sut.RecognizeAsync([1, 2, 3], 0, options));
            Assert.Contains("zzz", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Recognize_NullImage_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _sut.RecognizeAsync(null!, 0, new OcrOptions()));
    }

    [Fact]
    public async Task Dispose_ThenRecognize_Throws()
    {
        var engine = new TesseractOcrEngine();
        engine.Dispose();
        engine.Dispose(); // idempotent

        // Unavailable data fails before the pool is touched; with data present a disposed engine must refuse work.
        Assert.SkipWhen(BundledTessData is null, "bundled tessdata not found");
        var page = new RenderedPage(4, 4, 16, new byte[64]);
        await Assert.ThrowsAnyAsync<Exception>(
            () => engine.RecognizeRenderedAsync(page, 0, new OcrOptions { TessDataPath = BundledTessData }));
    }

    [Fact]
    public async Task RawPixelPath_MatchesPngPath_AndEnginesArePooled_WhenNativeTesseractIsAvailable()
    {
        Assert.SkipWhen(BundledTessData is null, "bundled tessdata not found");
        var options = new OcrOptions { TessDataPath = BundledTessData, OnlyScannedPages = false };
        var renderer = new PdfiumRenderer();
        var pdf = PdfFixtureBuilder.CreateWithText("The quick brown fox jumps over the lazy dog", 1);
        var raw = await renderer.RenderPageAsync(pdf, 0, options.Dpi);
        var png = await renderer.RenderPageToPngAsync(pdf, 0, options.Dpi);

        OcrPageResult viaPng, viaRaw;
        try
        {
            viaPng = await _sut.RecognizeAsync(png, 0, options);
            viaRaw = await _sut.RecognizeRenderedAsync(raw, 0, options);
        }
        catch (OcrUnavailableException ex)
        {
            Assert.Skip("native Tesseract not available: " + ex.Message);
            return;
        }

        Assert.Contains("quick", viaRaw.Text);
        Assert.Equal(viaPng.Words.Select(w => w.Text), viaRaw.Words.Select(w => w.Text));

        // Concurrent pages each rent their own engine and still agree.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => _sut.RecognizeRenderedAsync(raw, i, options)));
        Assert.All(results, r => Assert.Equal(viaRaw.Words.Select(w => w.Text), r.Words.Select(w => w.Text)));
        _sut.Dispose();
    }
}
