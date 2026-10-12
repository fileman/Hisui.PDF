using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Bookmarks (outline), links, and header/footer / Bates stamping.</summary>
public interface IPdfNavigationService
{
    Task<IReadOnlyList<PdfBookmark>> ReadOutlineAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Replaces the whole outline (an empty list removes it).</summary>
    Task<byte[]> WriteOutlineAsync(byte[] pdf, IReadOnlyList<PdfBookmark> outline, CancellationToken ct = default);

    /// <summary>Every link annotation of the document.</summary>
    Task<IReadOnlyList<PdfLink>> ReadLinksAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Adds an invisible-border link over <paramref name="rect"/> to a page (zero-based) or a web address — exactly one of them.</summary>
    Task<byte[]> AddLinkAsync(byte[] pdf, int pageIndex, PdfRect rect, int? targetPage, string? uri, CancellationToken ct = default);

    Task<byte[]> DeleteLinkAsync(byte[] pdf, int pageIndex, int annotIndex, CancellationToken ct = default);

    /// <summary>Draws header/footer text (already resolved per page, e.g. page numbers or Bates numbers) into the page content.</summary>
    Task<byte[]> StampAsync(byte[] pdf, IReadOnlyList<PageStamp> stamps, CancellationToken ct = default);
}
