using Hisui.Pdf.Core.Abstractions;
using PDFtoImage;
using SkiaSharp;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// <see cref="IPdfRenderer"/> backed by PDFium (via PDFtoImage). Rendering is synchronous and
/// CPU-bound, so calls are pushed onto the thread pool to keep the UI thread free.
/// </summary>
internal sealed class PdfiumRenderer : IPdfRenderer
{
    public Task<int> GetPageCountAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() => Conversion.GetPageCount(pdf), cancellationToken);
    }

    public Task<byte[]> RenderPageToPngAsync(byte[] pdf, int pageIndex, int dpi = 150, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);

        return Task.Run(
            () =>
            {
                using var output = new MemoryStream();
                var options = new RenderOptions
                {
                    Dpi = dpi,
                    WithAnnotations = true,
                    WithFormFill = true,
                    BackgroundColor = SKColors.White,
                };

                // int -> System.Index is implicit; this renders the page-from-start.
                Conversion.SavePng(output, pdf, page: pageIndex, password: null, options: options);
                return output.ToArray();
            },
            cancellationToken);
    }
}
