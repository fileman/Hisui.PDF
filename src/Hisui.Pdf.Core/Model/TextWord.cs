namespace Hisui.Pdf.Core.Model;

/// <summary>A word extracted from a PDF page, with its position in normalized screen coordinates.</summary>
public sealed class TextWord
{
    public required string Text { get; init; }
    public int PageIndex { get; init; }
    public PdfRect BoundingBox { get; init; }
    /// <summary>Approximate font size in points, from the first letter of the word.</summary>
    public double FontSizePoints { get; init; } = 12;
}
