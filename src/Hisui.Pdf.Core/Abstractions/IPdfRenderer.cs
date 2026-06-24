namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Renders PDF pages to raster images for the viewer and thumbnail strip. Output is PNG bytes so the
/// abstraction stays free of any imaging-library types.
/// </summary>
public interface IPdfRenderer
{
    /// <summary>Number of pages in the document.</summary>
    Task<int> GetPageCountAsync(byte[] pdf, CancellationToken cancellationToken = default);

    /// <summary>Renders a single zero-based page to PNG at the given DPI.</summary>
    Task<byte[]> RenderPageToPngAsync(byte[] pdf, int pageIndex, int dpi = 150, CancellationToken cancellationToken = default);
}
