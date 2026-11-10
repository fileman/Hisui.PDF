using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Converts a PDF to a Word document (.docx): text in reading order plus embedded images.</summary>
public interface IPdfWordExporter
{
    /// <summary>
    /// Builds a .docx from the PDF with default options (OCR on scanned pages when the engine is available).
    /// Layout is approximated: page size and margins, paragraphs with alignment and indents, font sizes, bold
    /// and colour, links, and pictures at their position in the flow. Tables and multi-column designs are
    /// flattened to reading order.
    /// </summary>
    Task<byte[]> ExportAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="ExportAsync(byte[], CancellationToken)"/>, with options. Scanned pages are rebuilt from OCR
    /// (text plus the logos, signatures and stamps cropped as pictures); when OCR is not available they fall
    /// back to their page images and <see cref="WordExportResult.OcrUnavailableReason"/> says why.
    /// </summary>
    Task<WordExportResult> ExportAsync(byte[] pdf, WordExportOptions? options, CancellationToken ct = default);
}
