namespace Hisui.Pdf.Core.Model;

/// <summary>Tunable options for an OCR run.</summary>
public sealed record OcrOptions
{
    /// <summary>
    /// Tesseract language code(s), e.g. <c>"eng"</c> or <c>"eng+ita"</c>. The matching
    /// <c>&lt;lang&gt;.traineddata</c> file must exist in the resolved tessdata directory.
    /// </summary>
    public string Language { get; init; } = "eng";

    /// <summary>
    /// Resolution used to rasterize pages before recognition. 300 DPI is the sweet spot for
    /// Tesseract; lower is faster but less accurate, higher rarely helps and costs memory.
    /// </summary>
    public int Dpi { get; init; } = 300;

    /// <summary>
    /// Directory containing the Tesseract <c>*.traineddata</c> files. When null the engine looks at
    /// the <c>TESSDATA_PREFIX</c> environment variable and then a <c>tessdata</c> folder beside the app.
    /// </summary>
    public string? TessDataPath { get; init; }

    /// <summary>
    /// When true (default) only pages that look scanned (an image with no real text layer) are
    /// processed. When false every page is run through OCR regardless of any existing text.
    /// </summary>
    public bool OnlyScannedPages { get; init; } = true;

    /// <summary>
    /// Maximum number of pages rasterized and recognized concurrently. <c>null</c> (default) picks
    /// <c>Clamp(ProcessorCount / 2, 1, 4)</c>: OCR is CPU-bound, but every in-flight page holds a ~35 MB
    /// 300 DPI bitmap plus Tesseract's working set, and half the cores stay free for the UI and for
    /// Tesseract's own threading. Use <c>1</c> to process pages strictly one after another.
    /// </summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>The concurrency actually used: <see cref="MaxDegreeOfParallelism"/>, or the default when unset (never below 1).</summary>
    internal int EffectiveMaxDegreeOfParallelism =>
        Math.Max(1, MaxDegreeOfParallelism ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
}
