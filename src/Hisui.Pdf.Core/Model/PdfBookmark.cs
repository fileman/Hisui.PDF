namespace Hisui.Pdf.Core.Model;

/// <summary>An outline (bookmark) entry. <see cref="PageIndex"/> is zero-based, -1 when it has no page destination.</summary>
public sealed record PdfBookmark(string Title, int PageIndex, IReadOnlyList<PdfBookmark> Children)
{
    public PdfBookmark(string title, int pageIndex) : this(title, pageIndex, []) { }
}

/// <summary>A link annotation: to a page of the document or to a web address.</summary>
public sealed record PdfLink
{
    public required int PageIndex { get; init; }
    /// <summary>Index in the page's /Annots array (valid until the document changes).</summary>
    public required int AnnotIndex { get; init; }
    /// <summary>Normalised, rendered orientation.</summary>
    public required PdfRect Rect { get; init; }
    /// <summary>Zero-based destination page, or null for a web link / unsupported action.</summary>
    public int? TargetPage { get; init; }
    public string? Uri { get; init; }
}

/// <summary>Text stamped on one page: header and footer slots (any may be empty).</summary>
public sealed record PageStamp
{
    public required int PageIndex { get; init; }
    public string TopLeft { get; init; } = "";
    public string TopCenter { get; init; } = "";
    public string TopRight { get; init; } = "";
    public string BottomLeft { get; init; } = "";
    public string BottomCenter { get; init; } = "";
    public string BottomRight { get; init; } = "";
    public double FontSize { get; init; } = 9;
    /// <summary>Distance from the page edge, in points.</summary>
    public double Margin { get; init; } = 24;
    public string ColorHex { get; init; } = "#000000";
}
