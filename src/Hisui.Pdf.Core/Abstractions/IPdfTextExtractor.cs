using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Text extraction and search backed by PDFium's text API (shares the renderer's open document).</summary>
public interface IPdfTextExtractor
{
    /// <summary>
    /// Extracts the plain text from the document. If <paramref name="pageIndex"/> is null, all pages
    /// are concatenated (separated by a form-feed character). Zero-based page index.
    /// </summary>
    Task<string> ExtractTextAsync(byte[] pdf, int? pageIndex = null, CancellationToken ct = default);

    /// <summary>Returns every word with its bounding box on the given zero-based page.</summary>
    Task<IReadOnlyList<TextWord>> ExtractWordsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default);

    /// <summary>
    /// Searches for <paramref name="query"/> across all pages and returns every match with the word boxes it
    /// spans (for highlight overlay). Matching is literal and phrase-aware: the query is matched against each
    /// page's text with words joined by single spaces, so multi-word phrases match across words and line
    /// breaks. <paramref name="options"/> toggles case-sensitivity and whole-word matching.
    /// </summary>
    /// <summary>Every page's words (outer list = pages), boxes normalised in rendered orientation.</summary>
    Task<IReadOnlyList<IReadOnlyList<TextWord>>> ExtractAllWordsAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Regular-expression search over each page's text (words joined by single spaces).</summary>
    Task<IReadOnlyList<TextSearchResult>> SearchPatternAsync(byte[] pdf, System.Text.RegularExpressions.Regex pattern, CancellationToken ct = default);

    Task<IReadOnlyList<TextSearchResult>> SearchAsync(
        byte[] pdf, string query, TextSearchOptions? options = null, CancellationToken ct = default);
}
