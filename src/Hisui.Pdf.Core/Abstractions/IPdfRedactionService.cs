using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Secure redaction: replaces each affected page with a rasterised image after burning black
/// rectangles over the sensitive regions. The underlying text/vector content is completely removed —
/// the redacted page becomes a flat image with no searchable text layer.
/// Trade-off: affected pages are larger (image bytes) and lose their text-select layer.
/// </summary>
public interface IPdfRedactionService
{
    /// <summary>
    /// Redacts the specified regions and returns a new PDF. Pages not listed are copied unchanged.
    /// </summary>
    /// <param name="pdf">Input PDF bytes.</param>
    /// <param name="regions">Pairs of zero-based page index and the normalized region to black out.</param>
    /// <param name="renderDpi">DPI used when rasterising the affected pages (default 300).</param>
    Task<byte[]> RedactAsync(
        byte[] pdf,
        IReadOnlyList<(int PageIndex, PdfRect Rect)> regions,
        int renderDpi = 300,
        CancellationToken ct = default);
}
