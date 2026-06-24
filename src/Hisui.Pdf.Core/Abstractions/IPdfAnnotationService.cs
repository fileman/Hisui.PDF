using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Overlay-based annotations and markup. All operations burn content into the page's content stream
/// (not as separate /Annots entries), so they survive any viewer and any subsequent merge/split.
/// All rectangles use <see cref="PdfRect"/> normalized page coordinates (0..1).
/// </summary>
public interface IPdfAnnotationService
{
    /// <summary>Stamps a diagonal text watermark on every page.</summary>
    Task<byte[]> AddTextWatermarkAsync(byte[] pdf, string text, WatermarkOptions? options = null, CancellationToken ct = default);

    /// <summary>Draws a semi-transparent highlight rectangle on a single page.</summary>
    Task<byte[]> AddHighlightAsync(byte[] pdf, int pageIndex, PdfRect rect, string colorHex = "#FFFF00", double opacity = 0.4, CancellationToken ct = default);

    /// <summary>Draws a rectangle outline (optionally filled) on a single page.</summary>
    Task<byte[]> AddRectangleAsync(byte[] pdf, int pageIndex, PdfRect rect, string strokeHex = "#FF0000", string? fillHex = null, double lineWidthPoints = 2.0, CancellationToken ct = default);

    /// <summary>Draws a free-text annotation box on a single page.</summary>
    Task<byte[]> AddFreeTextAsync(byte[] pdf, int pageIndex, PdfRect rect, string text, string colorHex = "#000000", double fontSizePoints = 12, CancellationToken ct = default);

    /// <summary>Draws a visual sticky-note box at a normalized position on a single page.</summary>
    Task<byte[]> AddStickyNoteAsync(byte[] pdf, int pageIndex, double normalizedX, double normalizedY, string noteText, CancellationToken ct = default);

    /// <summary>Draws an image (PNG/JPEG bytes) at a given rect on a single page.</summary>
    Task<byte[]> AddImageOverlayAsync(byte[] pdf, int pageIndex, byte[] imageBytes, PdfRect rect, CancellationToken ct = default);
}
