using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Overlay-based annotations and markup. All operations burn content into the page's content stream
/// (not as separate /Annots entries), so they survive any viewer and any subsequent merge/split.
/// All rectangles use <see cref="PdfRect"/> normalized page coordinates (0..1).
/// </summary>
public interface IPdfAnnotationService
{
    /// <summary>Stamps a diagonal text watermark on every page, replacing the one added earlier by this method.</summary>
    Task<byte[]> AddTextWatermarkAsync(byte[] pdf, string text, WatermarkOptions? options = null, CancellationToken ct = default);

    /// <summary>Text and settings of the watermark added by <see cref="AddTextWatermarkAsync"/>; null when there is none.</summary>
    Task<WatermarkInfo?> ReadWatermarkAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Removes the watermark added by <see cref="AddTextWatermarkAsync"/> from every page.</summary>
    Task<byte[]> RemoveWatermarkAsync(byte[] pdf, CancellationToken ct = default);

    // On every placed annotation `id` keeps the identity of one being redrawn after an edit, move or resize (null = a new one).

    /// <summary>Draws a semi-transparent highlight rectangle on a single page.</summary>
    Task<byte[]> AddHighlightAsync(byte[] pdf, int pageIndex, PdfRect rect, string colorHex = "#FFFF00", double opacity = 0.4, string? id = null, CancellationToken ct = default);

    /// <summary>Draws a rectangle outline (optionally filled) on a single page.</summary>
    Task<byte[]> AddRectangleAsync(byte[] pdf, int pageIndex, PdfRect rect, string strokeHex = "#FF0000", string? fillHex = null, double lineWidthPoints = 2.0, string? id = null, CancellationToken ct = default);

    /// <summary>
    /// Draws free text on a single page, one line per newline. With <paramref name="charPitchPoints"/> &gt; 0 each
    /// character is centred in its own cell of that width (for boxed forms such as IBAN fields); with
    /// <paramref name="background"/> false the text goes straight onto the page with no box behind it.
    /// </summary>
    Task<byte[]> AddFreeTextAsync(byte[] pdf, int pageIndex, PdfRect rect, string text, string colorHex = "#000000", double fontSizePoints = 12,
        double charPitchPoints = 0, bool background = true, bool bold = false, bool italic = false, string? id = null, CancellationToken ct = default);

    /// <summary>
    /// Every annotation placed on a page by this service (highlight, rectangle, free text, image/signature), in content
    /// order, with the settings it was drawn with so it can be moved, edited or removed.
    /// </summary>
    Task<IReadOnlyList<PlacedAnnotation>> ReadPlacedAsync(byte[] pdf, int pageIndex, CancellationToken ct = default);

    /// <summary>Removes the placed annotation with <paramref name="id"/> (from <see cref="ReadPlacedAsync"/>) from a page.</summary>
    Task<byte[]> RemovePlacedAsync(byte[] pdf, int pageIndex, string id, CancellationToken ct = default);

    /// <summary>Draws a visual sticky-note box at a normalized position on a single page.</summary>
    Task<byte[]> AddStickyNoteAsync(byte[] pdf, int pageIndex, double normalizedX, double normalizedY, string noteText, CancellationToken ct = default);

    /// <summary>Draws an image (PNG/JPEG bytes) at a given rect on a single page.</summary>
    Task<byte[]> AddImageOverlayAsync(byte[] pdf, int pageIndex, byte[] imageBytes, PdfRect rect, string? id = null, CancellationToken ct = default);
}
