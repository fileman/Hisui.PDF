using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Structural page operations over PDF byte buffers, built on PDFsharp page import/export.
/// </summary>
public interface IPdfPageService
{
    /// <summary>Number of pages in a document (cheap, structure-only).</summary>
    Task<int> GetPageCountAsync(byte[] pdf, CancellationToken cancellationToken = default);

    /// <summary>Concatenates several documents into one, preserving page order.</summary>
    Task<byte[]> MergeAsync(IReadOnlyList<byte[]> documents, CancellationToken cancellationToken = default);

    /// <summary>Produces a new document containing only the given zero-based pages, in the order given.</summary>
    Task<byte[]> ExtractPagesAsync(byte[] pdf, IReadOnlyList<int> pageIndices, CancellationToken cancellationToken = default);

    /// <summary>Splits a document into chunks of at most <paramref name="pagesPerChunk"/> pages each.</summary>
    Task<IReadOnlyList<byte[]>> SplitEveryAsync(byte[] pdf, int pagesPerChunk, CancellationToken cancellationToken = default);

    /// <summary>Materialises a <see cref="PdfDocumentSession"/> back into a single PDF.</summary>
    Task<byte[]> BuildFromSessionAsync(PdfDocumentSession session, CancellationToken cancellationToken = default);
}
