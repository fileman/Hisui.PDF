namespace Hisui.Pdf.Core.Model;

/// <summary>
/// A single word recognized by the OCR engine, positioned in normalized page coordinates
/// (see <see cref="PdfRect"/>) so it maps directly onto a rendered page or a PDFsharp page.
/// </summary>
public sealed class OcrWord
{
    public required string Text { get; init; }

    /// <summary>Word position in normalized page coordinates (top-left origin, Y down).</summary>
    public required PdfRect BoundingBox { get; init; }

    /// <summary>Recognition confidence in the range 0–100 (higher is better).</summary>
    public float Confidence { get; init; }
}
