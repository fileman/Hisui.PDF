using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Extracts raster images embedded inside a PDF document via PdfPig.</summary>
public interface IPdfImageExtractor
{
    /// <summary>
    /// Returns all embedded images. If <paramref name="pageIndex"/> is null, all pages are scanned.
    /// Images are returned as PNG bytes where possible; raw bytes otherwise.
    /// </summary>
    Task<IReadOnlyList<ExtractedImage>> ExtractImagesAsync(
        byte[] pdf, int? pageIndex = null, CancellationToken ct = default);
}
