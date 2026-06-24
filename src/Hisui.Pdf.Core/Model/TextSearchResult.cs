namespace Hisui.Pdf.Core.Model;

/// <summary>A single word-level match from a text search across a PDF document.</summary>
public sealed class TextSearchResult
{
    public required string MatchedText { get; init; }
    public int PageIndex { get; init; }
    public PdfRect BoundingBox { get; init; }
}
