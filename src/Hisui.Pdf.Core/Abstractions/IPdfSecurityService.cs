using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// AES-256 encryption and decryption of PDF documents (PDF revision 6 / V5).
/// Permissions follow Adobe's standard bit flags; see <see cref="PdfEncryptionOptions"/>.
/// </summary>
public interface IPdfSecurityService
{
    /// <summary>Returns <see langword="true"/> if the PDF byte stream is encrypted.</summary>
    bool IsEncrypted(byte[] pdf);

    /// <summary>Encrypts <paramref name="pdf"/> with AES-256 using the supplied options.</summary>
    Task<byte[]> EncryptAsync(byte[] pdf, PdfEncryptionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Removes encryption from <paramref name="pdf"/>. Accepts either the owner or the user password.
    /// When the owner password is provided the document structure is preserved in full; with only
    /// the user password a page-import rebuild is performed (annotations and forms are stripped).
    /// </summary>
    Task<byte[]> DecryptAsync(byte[] pdf, string password, CancellationToken ct = default);
}
