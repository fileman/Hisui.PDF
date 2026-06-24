namespace Hisui.Pdf.Core.Model;

/// <summary>
/// A reference to a single page inside one of the source documents tracked by a
/// <see cref="PdfDocumentSession"/>. Page operations mutate these refs (order, rotation, deletion)
/// without rewriting the underlying bytes until the session is materialised.
/// </summary>
public sealed class PdfPageRef
{
    public PdfPageRef(int sourceDocumentId, int sourcePageIndex, PageRotation rotation = PageRotation.None)
    {
        SourceDocumentId = sourceDocumentId;
        SourcePageIndex = sourcePageIndex;
        Rotation = rotation;
    }

    /// <summary>Id of the owning source document within the session.</summary>
    public int SourceDocumentId { get; }

    /// <summary>Zero-based page index within the source document.</summary>
    public int SourcePageIndex { get; }

    /// <summary>Extra rotation applied on top of the source page's own rotation.</summary>
    public PageRotation Rotation { get; set; }
}
