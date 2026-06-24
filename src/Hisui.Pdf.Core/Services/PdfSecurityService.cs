using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace Hisui.Pdf.Core.Services;

internal sealed class PdfSecurityService : IPdfSecurityService
{
    public bool IsEncrypted(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        // Use a password provider that immediately aborts; PDFsharp calls it only when the file
        // requires a password, so if it fires we know the document is encrypted.
        var providerInvoked = false;
        try
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import,
                args => { providerInvoked = true; args.Abort = true; });
            return doc.SecuritySettings.IsEncrypted;
        }
        catch
        {
            return providerInvoked;
        }
    }

    public Task<byte[]> EncryptAsync(byte[] pdf, PdfEncryptionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);

            var handler = doc.SecurityHandler;
            handler.OwnerPassword = options.OwnerPassword;
            handler.UserPassword = options.UserPassword;
            handler.SetEncryption(PdfDefaultEncryption.V5);

            var settings = doc.SecuritySettings;
            settings.PermitPrint = options.PermitPrint;
            settings.PermitAnnotations = options.PermitAnnotations;
            settings.PermitFormsFill = options.PermitFormsFill;
            settings.PermitExtractContent = options.PermitExtractContent;
            settings.PermitModifyDocument = options.PermitModifyDocument;
            settings.PermitAssembleDocument = options.PermitAssembleDocument;

            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }, ct);
    }

    public Task<byte[]> DecryptAsync(byte[] pdf, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(password);

        return Task.Run(() =>
        {
            // Try Modify mode (requires owner password) for a full-fidelity decrypt.
            // Fall back to Import+rebuild if only the user password is available.
            try
            {
                using var s = new MemoryStream(pdf, writable: false);
                using var doc = PdfReader.Open(s, password, PdfDocumentOpenMode.Modify);
                doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
                using var ms = new MemoryStream();
                doc.Save(ms);
                return ms.ToArray();
            }
            catch (PdfSharp.Pdf.IO.PdfReaderException)
            {
                // User password only: open for reading and rebuild as a fresh unencrypted document.
                using var s = new MemoryStream(pdf, writable: false);
                using var src = PdfReader.Open(s, password, PdfDocumentOpenMode.Import);
                using var dst = new PdfSharp.Pdf.PdfDocument();
                for (var i = 0; i < src.PageCount; i++)
                    dst.AddPage(src.Pages[i]);
                using var ms = new MemoryStream();
                dst.Save(ms);
                return ms.ToArray();
            }
        }, ct);
    }
}
