using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Real (non burnt-in) PDF markup annotations — comments other viewers list, edit and reply to. Comments are addressed
/// by page and /Annots index, valid until the document changes.
/// </summary>
public interface IPdfCommentService
{
    /// <summary>Every markup annotation of the document (sticky notes, highlights, shapes, ink, stamps…), replies included.</summary>
    Task<IReadOnlyList<PdfComment>> ReadCommentsAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Adds a sticky-note comment whose icon's top-left corner is at <paramref name="at"/> (normalised, rendered orientation).</summary>
    Task<byte[]> AddCommentAsync(byte[] pdf, int pageIndex, PdfRect at, string text, string author, CancellationToken ct = default);

    /// <summary>Replies to a comment (a hidden note linked with /IRT, as Acrobat does).</summary>
    Task<byte[]> ReplyAsync(byte[] pdf, int pageIndex, int annotIndex, string text, string author, CancellationToken ct = default);

    /// <summary>Changes a comment's text.</summary>
    Task<byte[]> SetTextAsync(byte[] pdf, int pageIndex, int annotIndex, string text, CancellationToken ct = default);

    /// <summary>Moves a comment by a normalised offset (its geometry — quads, vertices, ink — moves with it).</summary>
    Task<byte[]> MoveAsync(byte[] pdf, int pageIndex, int annotIndex, double dx, double dy, CancellationToken ct = default);

    /// <summary>Deletes a comment with its popup and its replies.</summary>
    Task<byte[]> DeleteAsync(byte[] pdf, int pageIndex, int annotIndex, CancellationToken ct = default);
}
