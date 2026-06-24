using Hisui.Pdf.Core.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Rewrites the PDF by importing all pages into a fresh document. PDFsharp applies zlib
/// compression to content streams on save, so PDFs with uncompressed streams or incremental-save
/// bloat typically shrink. Document-level artefacts (forms, bookmarks, security) are intentionally
/// stripped; use this only for print/archive workflows where the output is the final form.
/// </summary>
internal sealed class PdfOptimizer : IPdfOptimizer
{
    public Task<byte[]> OptimizeAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run(() =>
        {
            using var inStream = new MemoryStream(pdf, writable: false);
            using var src = PdfReader.Open(inStream, PdfDocumentOpenMode.Import);
            using var dst = new PdfDocument();

            for (var i = 0; i < src.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                dst.AddPage(src.Pages[i]);
            }

            using var ms = new MemoryStream();
            dst.Save(ms);
            return ms.ToArray();
        }, ct);
    }
}
