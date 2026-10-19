namespace Hisui.Pdf.Core.Model;

/// <summary>A markup annotation as listed in a comments panel.</summary>
public sealed record PdfComment
{
    public required int PageIndex { get; init; }
    /// <summary>Index in the page's /Annots array (valid until the document changes).</summary>
    public required int AnnotIndex { get; init; }
    /// <summary>PDF subtype without the slash: Text, FreeText, Highlight, Square, Ink…</summary>
    public required string Subtype { get; init; }
    /// <summary>Normalised, rendered orientation.</summary>
    public required PdfRect Rect { get; init; }
    public string Text { get; init; } = "";
    public string? Author { get; init; }
    public DateTime? Modified { get; init; }
    /// <summary>/Annots index of the comment this one replies to (same page), if any.</summary>
    public int? InReplyTo { get; init; }
    /// <summary>Hidden from the page (replies are).</summary>
    public bool IsHidden { get; init; }
}
