namespace Hisui.Pdf.Core.Abstractions;

/// <summary>PDF → Excel (.xlsx) and PowerPoint (.pptx).</summary>
public interface IPdfOfficeExporter
{
    /// <summary>One worksheet per page with the page's text arranged in rows and columns.</summary>
    Task<byte[]> ExportXlsxAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>One slide per page showing the page image (its text as alternative text).</summary>
    Task<byte[]> ExportPptxAsync(byte[] pdf, int dpi = 150, CancellationToken ct = default);
}
