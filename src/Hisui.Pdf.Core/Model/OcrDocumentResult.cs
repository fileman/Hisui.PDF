namespace Hisui.Pdf.Core.Model;

/// <summary>OCR result for a whole document: the per-page results plus document-wide helpers.</summary>
public sealed class OcrDocumentResult
{
    /// <summary>Per-page results, ordered by page index, for the pages that were processed.</summary>
    public IReadOnlyList<OcrPageResult> Pages { get; init; } = [];

    /// <summary>All page texts concatenated with a form-feed separator (mirrors the text extractor).</summary>
    public string Text => string.Join('\f', Pages.Select(p => p.Text));
}
