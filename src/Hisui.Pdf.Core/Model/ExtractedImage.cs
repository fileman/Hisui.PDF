namespace Hisui.Pdf.Core.Model;

/// <summary>An image extracted from a PDF page.</summary>
public sealed class ExtractedImage
{
    public int PageIndex { get; init; }
    public required byte[] Bytes { get; init; }
    /// <summary>"PNG", "JPEG", or "raw" when the format could not be determined.</summary>
    public required string Format { get; init; }
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public PdfRect BoundingBox { get; init; }
}
