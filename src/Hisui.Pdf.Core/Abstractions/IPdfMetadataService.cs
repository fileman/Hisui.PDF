using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Read and write the PDF Info dictionary (Title, Author, Subject, Keywords, Creator).
/// </summary>
public interface IPdfMetadataService
{
    /// <summary>Reads the document's Info dictionary. Fields missing from the PDF are returned as <see langword="null"/>.</summary>
    Task<PdfMetadata> ReadAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>
    /// Writes the supplied metadata fields into the Info dictionary.
    /// A <see langword="null"/> property in <paramref name="metadata"/> leaves the existing value unchanged.
    /// </summary>
    Task<byte[]> WriteAsync(byte[] pdf, PdfMetadata metadata, CancellationToken ct = default);
}
