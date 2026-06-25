namespace Hisui.Pdf.Core.Model;

/// <summary>A single text-search match: the matched text, its page, and the word boxes it spans.</summary>
public sealed class TextSearchResult
{
    public required string MatchedText { get; init; }
    public int PageIndex { get; init; }

    /// <summary>
    /// One normalized box per word fragment the match spans. A phrase match that crosses a line break
    /// yields several boxes (one per line); a single-word match yields one.
    /// </summary>
    public required IReadOnlyList<PdfRect> Boxes { get; init; }
}
