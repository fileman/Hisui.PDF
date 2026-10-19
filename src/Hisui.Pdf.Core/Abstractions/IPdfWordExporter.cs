namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Converts a PDF to a Word document (.docx): text in reading order plus embedded images.</summary>
public interface IPdfWordExporter
{
    /// <summary>
    /// Builds a .docx from the PDF. Layout is approximated (paragraphs, font sizes, images at the end of each
    /// page); tables and multi-column designs are flattened to reading order.
    /// </summary>
    Task<byte[]> ExportAsync(byte[] pdf, CancellationToken ct = default);
}
