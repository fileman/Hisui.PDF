using System.Text;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;
using Hisui.Pdf.Core.Tests.Fixtures;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
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
    public async Task MakeSearchable_TextLayerIsInvisible_UsesRenderModeThree()
    {
        // Guards the visual fix: the OCR text must be emitted with text rendering mode 3 (invisible),
        // never a painted/opaque fill — otherwise the glyphs show up as black marks over the scan.
        var pdf = PdfFixtureBuilder.CreateScanned();
        var engine = new FakeOcrEngine(("Hisui", new PdfRect(0.10, 0.10, 0.40, 0.16)));
        var sut = new PdfOcrService(_renderer, engine);

        var searchable = await sut.MakeSearchableAsync(pdf);

        using var doc = PdfReader.Open(new MemoryStream(searchable), PdfDocumentOpenMode.Modify);
        var content = doc.Pages[0].Contents.CreateSingleContent();
        var operators = Encoding.Latin1.GetString(content.Stream.UnfilteredValue);

        Assert.Contains("3 Tr", operators); // invisible text rendering mode
        Assert.Contains("Hisui", operators); // the recognized word is present in the content stream
    }

    [Fact]
    public async Task MakeSearchable_OnRotatedPage_MapsTextIntoContentSpace()
    {
        // Guards the rotation fix: scanned pages with a /Rotate flag (very common) must have the OCR text
        // mapped through a cm transform, or the selectable layer comes out rotated 90° from the visible text.
        var pdf = PdfFixtureBuilder.CreateScanned(rotate: 270);
        var engine = new FakeOcrEngine(("Ruotato", new PdfRect(0.10, 0.10, 0.40, 0.16)));
        var sut = new PdfOcrService(_renderer, engine);

        var searchable = await sut.MakeSearchableAsync(pdf);

        using var doc = PdfReader.Open(new MemoryStream(searchable), PdfDocumentOpenMode.Modify);
        var content = doc.Pages[0].Contents.CreateSingleContent();
        var operators = Encoding.Latin1.GetString(content.Stream.UnfilteredValue);

        Assert.Contains("0 -1 1 0 0", operators); // the /Rotate 270 display->content mapping
        Assert.Contains("Ruotato", operators);
    }

    [Fact]
    public async Task MakeSearchable_OnCroppedPage_OffsetsTextLayerByCropOrigin()
    {
        // Guards the CropBox fix: PDFium renders the CropBox region, so the text layer must be translated to
        // the crop origin — otherwise it mis-scales and drifts toward the page edges on cropped scans.
        var pdf = PdfFixtureBuilder.CreateScanned(cropInset: 40);
        var engine = new FakeOcrEngine(("Crop", new PdfRect(0.10, 0.10, 0.40, 0.16)));
        var sut = new PdfOcrService(_renderer, engine);

        var searchable = await sut.MakeSearchableAsync(pdf);

        using var doc = PdfReader.Open(new MemoryStream(searchable), PdfDocumentOpenMode.Modify);
        var content = doc.Pages[0].Contents.CreateSingleContent();
        var operators = Encoding.Latin1.GetString(content.Stream.UnfilteredValue);
        Assert.Contains("1 0 0 1 40 40", operators); // cm translation to the CropBox lower-left (40,40)
    }

    [Fact]
    public async Task MakeSearchable_DoesNotWriteDegenerateCropBox()
    {
        // Guards against the PdfPage.CropBox getter side effect: reading it materializes a zero-size
        // [0 0 0 0] CropBox onto pages without one, which Acrobat flags as an invalid/zero-size page.
        var pdf = PdfFixtureBuilder.CreateScanned(); // no CropBox on the page
        var engine = new FakeOcrEngine(("Hisui", new PdfRect(0.10, 0.10, 0.40, 0.16)));
        var sut = new PdfOcrService(_renderer, engine);

        var searchable = await sut.MakeSearchableAsync(pdf);

        using var doc = PdfReader.Open(new MemoryStream(searchable), PdfDocumentOpenMode.Import);
        var crop = doc.Pages[0].Elements.GetArray("/CropBox"); // raw read, no getter side effect
        var degenerate = crop is { Elements.Count: 4 }
            && crop.Elements.GetReal(2) - crop.Elements.GetReal(0) <= 0
            && crop.Elements.GetReal(3) - crop.Elements.GetReal(1) <= 0;
        Assert.False(degenerate, "OCR output must not contain a degenerate zero-size CropBox");
    }

    [Fact]
    public async Task MakeSearchable_PreservesEuroSign_ForSearchAndCopy()
    {
        // Guards the encoding fix: the € sign (WinAnsi byte 0x80) must survive into a searchable/copyable layer,
        // not degrade to '?'. Pervasive in Italian payroll/contract scans (salary tables).
        var pdf = PdfFixtureBuilder.CreateScanned();
        var engine = new FakeOcrEngine(("€1.250", new PdfRect(0.10, 0.10, 0.40, 0.16)));
        var sut = new PdfOcrService(_renderer, engine);

        var searchable = await sut.MakeSearchableAsync(pdf);

        var extracted = await new PdfTextExtractor().ExtractTextAsync(searchable);
        Assert.Contains("€", extracted);
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

    // ── parallelism ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Recognize_ManyPages_ResultsStayOrderedByPage_WithRandomizedDelays()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Digital", 16);
        var engine = new GateEngine { Delay = i => TimeSpan.FromMilliseconds((i * 37) % 23) };
        var sut = new PdfOcrService(_renderer, engine);

        var result = await sut.RecognizeAsync(pdf, new OcrOptions { OnlyScannedPages = false, MaxDegreeOfParallelism = 4 });

        Assert.Equal(Enumerable.Range(0, 16), result.Pages.Select(p => p.PageIndex));
        Assert.All(result.Pages, p => Assert.Equal($"page{p.PageIndex}", p.Text));
    }

    [Fact]
    public async Task Recognize_ConcurrencyIsBoundedByMaxDegreeOfParallelism_AndActuallyParallel()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Digital", 12);
        var engine = new GateEngine { Delay = _ => TimeSpan.FromMilliseconds(200) };
        var sut = new PdfOcrService(_renderer, engine);

        await sut.RecognizeAsync(pdf, new OcrOptions { OnlyScannedPages = false, MaxDegreeOfParallelism = 3 });

        // PDFium rendering is serialized, so the cap may not be reached on a slow box, but pages must overlap.
        Assert.InRange(engine.MaxConcurrent, 2, 3);
    }

    [Fact]
    public async Task Recognize_MaxDegreeOfParallelismOne_RunsSequentially()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Digital", 6);
        var engine = new GateEngine { Delay = _ => TimeSpan.FromMilliseconds(10) };
        var sut = new PdfOcrService(_renderer, engine);

        await sut.RecognizeAsync(pdf, new OcrOptions { OnlyScannedPages = false, MaxDegreeOfParallelism = 1 });

        Assert.Equal(1, engine.MaxConcurrent);
        Assert.Equal([0, 1, 2, 3, 4, 5], engine.StartOrder);
    }

    [Fact]
    public void DefaultMaxDegreeOfParallelism_IsBounded()
    {
        var dop = new OcrOptions().EffectiveMaxDegreeOfParallelism;
        Assert.InRange(dop, 1, 4);
        Assert.Equal(1, new OcrOptions { MaxDegreeOfParallelism = 0 }.EffectiveMaxDegreeOfParallelism);
    }

    [Fact]
    public async Task Recognize_WhenEngineIsUnavailable_SurfacesOcrUnavailableAndStopsEarly()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Digital", 40);
        var engine = new GateEngine { Delay = _ => TimeSpan.FromMilliseconds(20), FailOn = 0 };
        var sut = new PdfOcrService(_renderer, engine);

        await Assert.ThrowsAsync<OcrUnavailableException>(
            () => sut.RecognizeAsync(pdf, new OcrOptions { OnlyScannedPages = false, MaxDegreeOfParallelism = 4 }));

        Assert.True(engine.Started < 40, $"expected remaining pages to be skipped, but {engine.Started} started");
    }

    [Fact]
    public async Task Recognize_WhenEngineIsUnavailable_Sequential_SurfacesSameType()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Digital", 3);
        var engine = new GateEngine { FailOn = 1 };
        var sut = new PdfOcrService(_renderer, engine);

        await Assert.ThrowsAsync<OcrUnavailableException>(
            () => sut.MakeSearchableAsync(pdf, new OcrOptions { OnlyScannedPages = false, MaxDegreeOfParallelism = 1 }));
    }

    [Fact]
    public async Task Recognize_Cancellation_StopsWork()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Digital", 40);
        using var cts = new CancellationTokenSource();
        var engine = new GateEngine { Delay = _ => TimeSpan.FromMilliseconds(25), OnStart = _ => cts.CancelAfter(60) };
        var sut = new PdfOcrService(_renderer, engine);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.RecognizeAsync(pdf, new OcrOptions { OnlyScannedPages = false, MaxDegreeOfParallelism = 4 }, cts.Token));

        Assert.True(engine.Started < 40, $"expected cancellation to stop work, but {engine.Started} started");
    }

    [Fact]
    public async Task MakeSearchable_ParallelOutputMatchesSequential()
    {
        var pdf = PdfFixtureBuilder.CreateScanned();
        var words = new (string, PdfRect)[] { ("Hisui", new PdfRect(0.10, 0.10, 0.40, 0.16)), ("Scan", new PdfRect(0.5, 0.2, 0.7, 0.25)) };
        var sequential = await new PdfOcrService(_renderer, new FakeOcrEngine(words))
            .MakeSearchableAsync(pdf, new OcrOptions { MaxDegreeOfParallelism = 1 });
        var parallel = await new PdfOcrService(_renderer, new FakeOcrEngine(words))
            .MakeSearchableAsync(pdf, new OcrOptions { MaxDegreeOfParallelism = 4 });

        var ex = new PdfTextExtractor();
        Assert.Equal(await ex.ExtractTextAsync(sequential), await ex.ExtractTextAsync(parallel));
    }

    [Fact]
    public async Task MakeSearchable_MultiPage_PlacesEachPagesTextOnItsOwnPage()
    {
        var pdf = PdfFixtureBuilder.CreateWithText("Digital", 8);
        var engine = new GateEngine { Delay = i => TimeSpan.FromMilliseconds((8 - i) * 5), Word = "w" };
        var sut = new PdfOcrService(_renderer, engine);

        var searchable = await sut.MakeSearchableAsync(pdf, new OcrOptions { OnlyScannedPages = false, MaxDegreeOfParallelism = 4 });

        var extractor = new PdfTextExtractor();
        for (var i = 0; i < 8; i++) Assert.Contains($"page{i}", await extractor.ExtractTextAsync(searchable, i));
    }

    /// <summary>
    /// Engine double that records concurrency, start order and cancellation, with a per-page delay and an
    /// optional page on which it throws <see cref="OcrUnavailableException"/>. Returns <c>page{index}</c> as text.
    /// </summary>
    private sealed class GateEngine : IOcrEngine
    {
        private readonly object _gate = new();
        private int _running, _maxConcurrent, _started;
        private readonly List<int> _startOrder = [];

        public Func<int, TimeSpan> Delay { get; init; } = _ => TimeSpan.Zero;
        public Action<int>? OnStart { get; init; }
        public int FailOn { get; init; } = -1;
        public string? Word { get; init; }

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public int Started => Volatile.Read(ref _started);
        public List<int> StartOrder { get { lock (_gate) return [.. _startOrder]; } }

        public async Task<OcrPageResult> RecognizeAsync(byte[] pageImagePng, int pageIndex, OcrOptions options, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            int now;
            lock (_gate)
            {
                _startOrder.Add(pageIndex);
                now = ++_running;
                if (now > _maxConcurrent) _maxConcurrent = now;
            }
            Interlocked.Increment(ref _started);
            try
            {
                OnStart?.Invoke(pageIndex);
                await Task.Delay(Delay(pageIndex), ct);
                if (pageIndex == FailOn) throw new OcrUnavailableException("engine unavailable");
                var text = $"page{pageIndex}";
                return new OcrPageResult
                {
                    PageIndex = pageIndex,
                    Text = text,
                    MeanConfidence = 90,
                    Words = [new OcrWord { Text = text, BoundingBox = new PdfRect(0.1, 0.1, 0.5, 0.15), Confidence = 90 }],
                };
            }
            finally
            {
                lock (_gate) _running--;
            }
        }
    }

    /// <summary>
    /// Test double for <see cref="IOcrEngine"/>: records which pages it was asked to process and
    /// returns a fixed set of words, so the orchestration can be tested without native Tesseract.
    /// </summary>
    private sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly (string Text, PdfRect Box)[] _words;
        private readonly object _gate = new();
        private readonly List<int> _processed = [];
        public List<int> ProcessedPages { get { lock (_gate) return [.. _processed.Order()]; } }

        public FakeOcrEngine(params (string Text, PdfRect Box)[] words) => _words = words;

        public Task<OcrPageResult> RecognizeAsync(byte[] pageImagePng, int pageIndex, OcrOptions options, CancellationToken ct = default)
        {
            lock (_gate) _processed.Add(pageIndex);
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
