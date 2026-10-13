using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Tesseract;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// <see cref="IOcrEngine"/> backed by the Tesseract 5 engine. Recognition is native and CPU-bound,
/// so each call runs on the thread pool. The native libraries (libtesseract / libleptonica) and the
/// <c>*.traineddata</c> language files are required at runtime; when either is missing the engine
/// surfaces a clear <see cref="OcrUnavailableException"/> rather than a raw P/Invoke failure.
/// Initialized <c>TesseractEngine</c> instances (not thread-safe, expensive to create) are pooled per
/// language and data folder, so concurrent pages each rent their own and nothing reloads per page.
/// </summary>
internal sealed class TesseractOcrEngine : IOcrEngine, IDisposable
{
    // Idle engines kept per (tessdata, language). Each holds the loaded traineddata (tens of MB), so the
    // cap matches the highest default page concurrency rather than growing with the core count.
    private const int MaxIdleEnginesPerKey = 4;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string TessData, string Language), Stack<TesseractEngine>> _idle = [];
    private bool _disposed;

    public Task<OcrPageResult> RecognizeAsync(byte[] pageImagePng, int pageIndex, OcrOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pageImagePng);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() => Recognize(Pix.LoadFromMemory, pageImagePng, pageIndex, options, ct), ct);
    }

    public Task<OcrPageResult> RecognizeRenderedAsync(RenderedPage page, int pageIndex, OcrOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() => Recognize(ToGrayPix, page, pageIndex, options, ct), ct);
    }

    public void Dispose()
    {
        List<TesseractEngine> toDispose;
        lock (_gate)
        {
            _disposed = true;
            toDispose = [.. _idle.Values.SelectMany(s => s)];
            _idle.Clear();
        }
        foreach (var engine in toDispose) engine.Dispose();
    }

    private OcrPageResult Recognize<TImage>(Func<TImage, Pix> load, TImage image, int pageIndex, OcrOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tessData = ResolveTessData(options);
        if (options.EffectiveMaxDegreeOfParallelism > 1) LimitNativeThreads();

        try
        {
            var key = (tessData, options.Language);
            var engine = Rent(key);
            var healthy = false;
            try
            {
                using var pix = load(image);
                var result = Process(engine, pix, pageIndex, ct);
                healthy = true;
                return result;
            }
            catch (OperationCanceledException)
            {
                healthy = true; // cancelled between native calls; the engine itself is fine
                throw;
            }
            finally
            {
                // An engine that threw from native code is dropped rather than recycled.
                if (healthy) Return(key, engine); else engine.Dispose();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TesseractException)
        {
            // Native library missing/mismatched, or the engine refused the language data.
            throw new OcrUnavailableException(UnavailableMessage(tessData, options.Language), ex);
        }
    }

    private static OcrPageResult Process(TesseractEngine engine, Pix pix, int pageIndex, CancellationToken ct)
    {
        using var page = engine.Process(pix);

        int width = pix.Width, height = pix.Height;
        var words = new List<OcrWord>();

        using (var iter = page.GetIterator())
        {
            iter.Begin();
            do
            {
                ct.ThrowIfCancellationRequested();
                if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var box)) continue;

                var text = iter.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(text)) continue;

                words.Add(new OcrWord
                {
                    Text = text,
                    Confidence = iter.GetConfidence(PageIteratorLevel.Word),
                    BoundingBox = Normalize(box, width, height),
                });
            }
            while (iter.Next(PageIteratorLevel.Word));
        }

        return new OcrPageResult
        {
            PageIndex = pageIndex,
            Words = words,
            Text = page.GetText() ?? string.Empty,
            MeanConfidence = page.GetMeanConfidence(),
        };
    }

    /// <summary>
    /// Takes an idle engine for the key or creates one. A <see cref="TesseractEngine"/> is not thread-safe, so an
    /// engine is owned by exactly one page at a time; creating one reloads the traineddata, which is what pooling avoids.
    /// </summary>
    private TesseractEngine Rent((string TessData, string Language) key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_idle.TryGetValue(key, out var stack) && stack.TryPop(out var engine)) return engine;
        }
        return new TesseractEngine(key.TessData, key.Language, EngineMode.Default);
    }

    private void Return((string TessData, string Language) key, TesseractEngine engine)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                if (!_idle.TryGetValue(key, out var stack)) _idle[key] = stack = new Stack<TesseractEngine>();
                if (stack.Count < MaxIdleEnginesPerKey)
                {
                    stack.Push(engine);
                    return;
                }
            }
        }
        engine.Dispose();
    }

    /// <summary>
    /// Builds an 8-bit grayscale Leptonica image from BGRA pixels (Rec. 601 luma). Tesseract binarizes
    /// internally anyway, so grayscale loses nothing and is a quarter of the size of 32-bit RGBA.
    /// </summary>
    private static unsafe Pix ToGrayPix(RenderedPage page)
    {
        var pix = Pix.Create(page.Width, page.Height, 8);
        try
        {
            var data = pix.GetData();
            var src = page.Bgra;
            for (var y = 0; y < page.Height; y++)
            {
                var line = (uint*)data.Data + (long)y * data.WordsPerLine;
                var o = y * page.Stride;
                for (var x = 0; x < page.Width; x++, o += 4)
                {
                    var gray = (src[o + 2] * 77 + src[o + 1] * 150 + src[o] * 29) >> 8;
                    // SetDataByte hides Leptonica's big-endian-within-word pixel packing.
                    PixData.SetDataByte(line, x, (uint)gray);
                }
            }
            return pix;
        }
        catch
        {
            pix.Dispose();
            throw;
        }
    }

    private static int _nativeThreadsLimited;

    /// <summary>
    /// Tesseract (via OpenMP) can spawn its own worker threads per recognition. With several pages already
    /// running in parallel that multiplies into cores-squared threads that only fight each other, so cap
    /// OpenMP at one thread per page unless the user chose a limit. OpenMP reads the variable once when its
    /// runtime loads, so this must run before the first engine exists. On Unix .NET's
    /// <see cref="Environment.SetEnvironmentVariable(string, string?)"/> only changes the managed copy, which
    /// native code never sees, so the real process environment is set through libc as well.
    /// </summary>
    private static void LimitNativeThreads()
    {
        if (Interlocked.Exchange(ref _nativeThreadsLimited, 1) != 0) return;
        const string name = "OMP_THREAD_LIMIT";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return;

        Environment.SetEnvironmentVariable(name, "1");
        if (OperatingSystem.IsWindows()) return;
        try { setenv(name, "1", 0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* best effort */ }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setenv")]
    private static extern int setenv(string name, string value, int overwrite);

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the directory holding the <c>*.traineddata</c> files and verifies the requested
    /// language(s) are present, so callers get an actionable error before the native engine starts.
    /// </summary>
    private static string ResolveTessData(OcrOptions options)
    {
        string?[] candidates =
        [
            options.TessDataPath,
            Environment.GetEnvironmentVariable("TESSDATA_PREFIX"),
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
        ];

        var dir = Array.Find(candidates, c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));
        if (dir is null)
            throw new OcrUnavailableException(
                "OCR language data was not found. Place the Tesseract '<lang>.traineddata' files in a " +
                "'tessdata' folder next to the application, or set the TESSDATA_PREFIX environment variable " +
                "(download from https://github.com/tesseract-ocr/tessdata_fast).");

        foreach (var lang in options.Language.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!File.Exists(Path.Combine(dir, lang + ".traineddata")))
                throw new OcrUnavailableException(
                    $"OCR language '{lang}' is not installed. Add '{lang}.traineddata' to '{dir}' " +
                    "(download from https://github.com/tesseract-ocr/tessdata_fast).");
        }

        return dir;
    }

    /// <summary>Converts a pixel-space Tesseract rect to a normalized top-left-origin <see cref="PdfRect"/>.</summary>
    private static PdfRect Normalize(Rect box, int width, int height)
    {
        if (width <= 0 || height <= 0) return new PdfRect(0, 0, 0, 0);
        return new PdfRect(
            Left:   (double)box.X1 / width,
            Top:    (double)box.Y1 / height,
            Right:  (double)box.X2 / width,
            Bottom: (double)box.Y2 / height);
    }

    private static string UnavailableMessage(string tessData, string language) =>
        $"The OCR engine could not start (language '{language}', data folder '{tessData}'). Ensure the " +
        "native Tesseract/Leptonica libraries are installed (Linux: 'libtesseract'/'libleptonica'; " +
        "macOS: 'brew install tesseract leptonica') and the language data file is valid.";
}
