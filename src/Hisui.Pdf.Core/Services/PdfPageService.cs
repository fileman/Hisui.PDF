using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Structural page operations built on PDFsharp page import. Each operation opens its inputs in
/// <see cref="PdfDocumentOpenMode.Import"/>, copies the wanted pages into a fresh document and saves
/// it to a byte buffer. Work runs on the thread pool.
/// </summary>
internal sealed class PdfPageService : IPdfPageService
{
    public Task<int> GetPageCountAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(
            () =>
            {
                using var doc = OpenImport(pdf);
                return doc.PageCount;
            },
            cancellationToken);
    }

    public Task<byte[]> MergeAsync(IReadOnlyList<byte[]> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return Task.Run(
            () =>
            {
                using var output = new PdfDocument();
                foreach (var bytes in documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var input = OpenImport(bytes);
                    for (var i = 0; i < input.PageCount; i++)
                        output.AddPage(input.Pages[i]);
                }

                return Save(output);
            },
            cancellationToken);
    }

    public Task<byte[]> ExtractPagesAsync(byte[] pdf, IReadOnlyList<int> pageIndices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(pageIndices);
        return Task.Run(
            () =>
            {
                using var input = OpenImport(pdf);
                using var output = new PdfDocument();
                foreach (var index in pageIndices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index < 0 || index >= input.PageCount)
                        throw new ArgumentOutOfRangeException(
                            nameof(pageIndices), $"Page index {index} is out of range (0..{input.PageCount - 1}).");
                    output.AddPage(input.Pages[index]);
                }

                return Save(output);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<byte[]>> SplitEveryAsync(byte[] pdf, int pagesPerChunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pagesPerChunk);
        return Task.Run<IReadOnlyList<byte[]>>(
            () =>
            {
                using var input = OpenImport(pdf);
                var chunks = new List<byte[]>();
                for (var start = 0; start < input.PageCount; start += pagesPerChunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var output = new PdfDocument();
                    var end = Math.Min(start + pagesPerChunk, input.PageCount);
                    for (var i = start; i < end; i++)
                        output.AddPage(input.Pages[i]);
                    chunks.Add(Save(output));
                }

                return chunks;
            },
            cancellationToken);
    }

    public Task<byte[]> BuildFromSessionAsync(PdfDocumentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.Run(
            () =>
            {
                // Open each source document once and reuse it across all of its page refs.
                var importers = new Dictionary<int, PdfDocument>();
                try
                {
                    using var output = new PdfDocument();
                    foreach (var pageRef in session.Pages)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!importers.TryGetValue(pageRef.SourceDocumentId, out var source))
                        {
                            source = OpenImport(session.GetSource(pageRef.SourceDocumentId));
                            importers[pageRef.SourceDocumentId] = source;
                        }

                        var added = output.AddPage(source.Pages[pageRef.SourcePageIndex]);
                        if (pageRef.Rotation != PageRotation.None)
                            added.Rotate = (added.Rotate + (int)pageRef.Rotation) % 360;
                    }

                    return Save(output);
                }
                finally
                {
                    foreach (var importer in importers.Values)
                        importer.Dispose();
                }
            },
            cancellationToken);
    }

    private static PdfDocument OpenImport(byte[] bytes)
    {
        // A fresh, read-only buffer per open — PdfPig and PDFsharp must never share a stream handle.
        var stream = new MemoryStream(bytes, writable: false);
        return PdfReader.Open(stream, PdfDocumentOpenMode.Import);
    }

    private static byte[] Save(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }
}
