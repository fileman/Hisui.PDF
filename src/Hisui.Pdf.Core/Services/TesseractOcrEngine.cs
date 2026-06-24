using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Tesseract;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// <see cref="IOcrEngine"/> backed by the Tesseract 5 engine. Recognition is native and CPU-bound,
/// so each call runs on the thread pool. The native libraries (libtesseract / libleptonica) and the
/// <c>*.traineddata</c> language files are required at runtime; when either is missing the engine
/// surfaces a clear <see cref="OcrUnavailableException"/> rather than a raw P/Invoke failure.
/// </summary>
internal sealed class TesseractOcrEngine : IOcrEngine
{
    public Task<OcrPageResult> RecognizeAsync(byte[] pageImagePng, int pageIndex, OcrOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pageImagePng);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var tessData = ResolveTessData(options);

            try
            {
                using var engine = new TesseractEngine(tessData, options.Language, EngineMode.Default);
                using var pix = Pix.LoadFromMemory(pageImagePng);
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
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TesseractException)
            {
                // Native library missing/mismatched, or the engine refused the language data.
                throw new OcrUnavailableException(UnavailableMessage(tessData, options.Language), ex);
            }
        }, ct);
    }

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
