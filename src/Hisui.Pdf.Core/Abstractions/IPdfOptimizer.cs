using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// PDF size reduction. Recompresses embedded JPEG images (decode, optional downscale, re-encode at a
/// lower quality) — the dominant cost in scanned / image-heavy PDFs — while preserving the document
/// structure, including any text layer added by OCR.
/// </summary>
public interface IPdfOptimizer
{
    Task<byte[]> OptimizeAsync(byte[] pdf, CompressionOptions? options = null, CancellationToken ct = default);
}
