using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// OCR for scanned PDFs. Detects image-only pages, recognizes their text, and produces a
/// "searchable PDF" by overlaying an invisible, selectable text layer on top of the scanned image.
/// </summary>
public interface IPdfOcrService
{
    /// <summary>
    /// Returns the zero-based indices of pages that look scanned: they contain a raster image but no
    /// meaningful extractable text layer. These are the pages OCR can usefully add text to.
    /// </summary>
    Task<IReadOnlyList<int>> DetectScannedPagesAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>
    /// Runs OCR and returns the recognized text and word boxes per page, without modifying the PDF.
    /// By default only scanned pages are processed (see <see cref="OcrOptions.OnlyScannedPages"/>).
    /// </summary>
    /// <exception cref="OcrUnavailableException">The OCR engine or language data is not available.</exception>
    Task<OcrDocumentResult> RecognizeAsync(byte[] pdf, OcrOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Produces a searchable copy of <paramref name="pdf"/>: the visible page content is unchanged,
    /// but an invisible text layer from OCR is added so the text can be selected, searched and copied.
    /// Pages that already have a text layer are left untouched (unless
    /// <see cref="OcrOptions.OnlyScannedPages"/> is false).
    /// </summary>
    /// <exception cref="OcrUnavailableException">The OCR engine or language data is not available.</exception>
    Task<byte[]> MakeSearchableAsync(byte[] pdf, OcrOptions? options = null, CancellationToken ct = default);
}
