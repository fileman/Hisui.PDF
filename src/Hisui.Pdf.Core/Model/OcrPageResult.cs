namespace Hisui.Pdf.Core.Model;

/// <summary>OCR result for a single page: the recognized words (with boxes) and the joined text.</summary>
public sealed class OcrPageResult
{
    public required int PageIndex { get; init; }

    /// <summary>Recognized words with their bounding boxes, in reading order where available.</summary>
    public IReadOnlyList<OcrWord> Words { get; init; } = [];

    /// <summary>The full recognized text of the page (engine-provided layout).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Mean recognition confidence across the page in the range 0–100.</summary>
    public float MeanConfidence { get; init; }
}
