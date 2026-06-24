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
}
