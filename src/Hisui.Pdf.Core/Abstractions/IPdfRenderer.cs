using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Renders PDF pages to raster images for the viewer and thumbnail strip. Hot UI paths use the raw
/// pixel API (<see cref="RenderPageAsync"/>); the PNG API remains for consumers that need an encoded
/// image (OCR, redaction, print, export) and keeps the abstraction free of any imaging-library types.
/// </summary>
public interface IPdfRenderer
{
    /// <summary>Number of pages in the document.</summary>
    Task<int> GetPageCountAsync(byte[] pdf, CancellationToken cancellationToken = default);

    /// <summary>Renders a single zero-based page to PNG at the given DPI.</summary>
    Task<byte[]> RenderPageToPngAsync(byte[] pdf, int pageIndex, int dpi = 150, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a single zero-based page to raw BGRA pixels at the given DPI, skipping the PNG
    /// encode/decode round trip. The opened document is cached by <paramref name="pdf"/> reference
    /// identity, so the array must not be mutated while it is in use (pass a new array for new content).
    /// </summary>
    Task<RenderedPage> RenderPageAsync(byte[] pdf, int pageIndex, int dpi = 150, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="RenderPageAsync(byte[], int, int, CancellationToken)"/>, with colour handling and
    /// print-mode rendering from <paramref name="options"/>. Never modifies the document.
    /// </summary>
    Task<RenderedPage> RenderPageAsync(byte[] pdf, int pageIndex, int dpi, PdfRenderOptions options, CancellationToken cancellationToken = default);

    /// <summary>Every page's displayed size in points (rotation applied), in page order.</summary>
    Task<IReadOnlyList<PdfPageSize>> GetPageSizesAsync(byte[] pdf, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the cached document for <paramref name="pdf"/>, if any, so its buffer can be collected.
    /// Optional: the renderer also evicts least-recently-used documents on its own.
    /// </summary>
    void Release(byte[] pdf);
}
