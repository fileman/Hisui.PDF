using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Services;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Low-level OCR boundary: turns a rasterized page image into recognized text and word boxes.
/// Isolating the native OCR backend behind this interface keeps <see cref="IPdfOcrService"/>
/// orchestration testable and lets the engine be swapped or mocked.
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// Recognizes text in a single page image (PNG bytes). Returned word boxes are in normalized
    /// page coordinates (0–1, top-left origin) so they map straight onto the source PDF page.
    /// </summary>
    /// <exception cref="OcrUnavailableException">
    /// The native engine or the language data is not available.
    /// </exception>
    Task<OcrPageResult> RecognizeAsync(byte[] pageImagePng, int pageIndex, OcrOptions options, CancellationToken ct = default);

    /// <summary>
    /// Recognizes text in a rasterized page given as raw pixels, skipping the PNG encode/decode round trip.
    /// Same contract as the PNG overload. The default implementation encodes to PNG and forwards, so
    /// engines that only understand encoded images keep working; native engines override it.
    /// </summary>
    /// <exception cref="OcrUnavailableException">
    /// The native engine or the language data is not available.
    /// </exception>
    Task<OcrPageResult> RecognizeRenderedAsync(RenderedPage page, int pageIndex, OcrOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return RecognizeAsync(PdfiumRenderer.EncodePng(page), pageIndex, options, ct);
    }
}
