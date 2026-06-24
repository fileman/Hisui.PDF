using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

internal sealed class PdfMetadataService : IPdfMetadataService
{
    public Task<PdfMetadata> ReadAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            var info = doc.Info;
            return new PdfMetadata
            {
                Title    = NullIfEmpty(info.Title),
                Author   = NullIfEmpty(info.Author),
                Subject  = NullIfEmpty(info.Subject),
                Keywords = NullIfEmpty(info.Keywords),
                Creator  = NullIfEmpty(info.Creator),
            };
        }, ct);
    }

    public Task<byte[]> WriteAsync(byte[] pdf, PdfMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(metadata);

        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            var info = doc.Info;

            if (metadata.Title    is not null) info.Title    = metadata.Title;
            if (metadata.Author   is not null) info.Author   = metadata.Author;
            if (metadata.Subject  is not null) info.Subject  = metadata.Subject;
            if (metadata.Keywords is not null) info.Keywords = metadata.Keywords;
            if (metadata.Creator  is not null) info.Creator  = metadata.Creator;

            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }, ct);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
