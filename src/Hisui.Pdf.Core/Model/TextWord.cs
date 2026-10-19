namespace Hisui.Pdf.Core.Model;

/// <summary>A word extracted from a PDF page, with its position in normalized screen coordinates.</summary>
public sealed class TextWord
{
    public required string Text { get; init; }
    public int PageIndex { get; init; }
    public PdfRect BoundingBox { get; init; }
    /// <summary>Approximate font size in points, from the first letter of the word.</summary>
    public double FontSizePoints { get; init; } = 12;
    /// <summary>False for rotated text (e.g. vertical column headers).</summary>
    public bool IsHorizontal { get; init; } = true;
    /// <summary>Fill colour of the first letter as 0xAARRGGBB (opaque black when unknown).</summary>
    public uint ColorArgb { get; init; } = 0xFF000000;
    /// <summary>True when the first letter's font is bold (weight 600 or more).</summary>
    public bool IsBold { get; init; }
}
