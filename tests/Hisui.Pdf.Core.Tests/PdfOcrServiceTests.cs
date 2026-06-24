using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using Xunit;

namespace Hisui.Pdf.Core.Tests;

public class PdfOcrServiceTests
{
    private readonly PdfiumRenderer _renderer = new();

    [Fact]
    public async Task DetectScannedPages_OnImageOnlyPdf_ReturnsThePage()
    {
        var pdf = PdfFixtureBuilder.CreateScanned();
        var sut = new PdfOcrService(_renderer, new FakeOcrEngine());

        var scanned = await sut.DetectScannedPagesAsync(pdf);

        Assert.Equal([0], scanned);
    }

    [Fact]
    public async Task DetectScannedPages_OnTextPdf_ReturnsEmpty()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("This is a real digital text layer for the page", 1);
        var sut = new PdfOcrService(_renderer, new FakeOcrEngine());

        var scanned = await sut.DetectScannedPagesAsync(pdf);

        Assert.Empty(scanned);
    }

    [Fact]
    public async Task Recognize_OnlyScannedPages_RunsEngineOnDetectedPagesOnly()
    {
        var pdf = PdfFixtureBuilder.CreateScanned();
        var engine = new FakeOcrEngine(("Recognized", new PdfRect(0.1, 0.1, 0.4, 0.16)));
        var sut = new PdfOcrService(_renderer, engine);

        var result = await sut.RecognizeAsync(pdf); // OnlyScannedPages = true by default

        Assert.Equal([0], engine.ProcessedPages);
        var page = Assert.Single(result.Pages);
        Assert.Equal(0, page.PageIndex);
        Assert.Contains("Recognized", result.Text);
    }

    [Fact]
    public async Task Recognize_WhenOnlyScannedPagesFalse_ProcessesEveryPage()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Already digital", 3);
        var engine = new FakeOcrEngine(("Word", new PdfRect(0.1, 0.1, 0.3, 0.15)));
        var sut = new PdfOcrService(_renderer, engine);

        var result = await sut.RecognizeAsync(pdf, new OcrOptions { OnlyScannedPages = false });

        Assert.Equal([0, 1, 2], engine.ProcessedPages);
        Assert.Equal(3, result.Pages.Count);
    }

    [Fact]
    public async Task MakeSearchable_AddsSelectableTextLayer_ReadableByExtractor()
    {
        var pdf = PdfFixtureBuilder.CreateScanned();
        var engine = new FakeOcrEngine(("Hisui", new PdfRect(0.10, 0.10, 0.40, 0.16)));
        var sut = new PdfOcrService(_renderer, engine);

        var searchable = await sut.MakeSearchableAsync(pdf);

        // The output is a different document (text layer was added)…
        Assert.NotEqual(pdf.Length, searchable.Length);
        // …and the previously image-only page now yields extractable text.
        var extracted = await new PdfTextExtractor().ExtractTextAsync(searchable);
        Assert.Contains("Hisui", extracted);
    }

    [Fact]
    public async Task MakeSearchable_WithNoScannedPages_ReturnsInputUnchanged()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Fully digital document text", 1);
        var engine = new FakeOcrEngine(("ShouldNotRun", new PdfRect(0, 0, 1, 1)));
        var sut = new PdfOcrService(_renderer, engine);

        var result = await sut.MakeSearchableAsync(pdf);

        Assert.Same(pdf, result);
        Assert.Empty(engine.ProcessedPages);
    }

    [Fact]
    public async Task Recognize_NullPdf_Throws()
    {
        var sut = new PdfOcrService(_renderer, new FakeOcrEngine());
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RecognizeAsync(null!));
    }

    /// <summary>
    /// Test double for <see cref="IOcrEngine"/>: records which pages it was asked to process and
    /// returns a fixed set of words, so the orchestration can be tested without native Tesseract.
    /// </summary>
    private sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly (string Text, PdfRect Box)[] _words;
        public List<int> ProcessedPages { get; } = [];

        public FakeOcrEngine(params (string Text, PdfRect Box)[] words) => _words = words;

        public Task<OcrPageResult> RecognizeAsync(byte[] pageImagePng, int pageIndex, OcrOptions options, CancellationToken ct = default)
        {
            ProcessedPages.Add(pageIndex);
            var words = _words
                .Select(w => new OcrWord { Text = w.Text, BoundingBox = w.Box, Confidence = 90 })
                .ToList();
            return Task.FromResult(new OcrPageResult
            {
                PageIndex = pageIndex,
                Words = words,
                Text = string.Join(' ', words.Select(w => w.Text)),
                MeanConfidence = 90,
            });
        }
    }
}
