using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>Certificate-based (PAdES) digital signatures: sign, and verify the signatures in a document.</summary>
public interface IPdfSignatureService
{
    /// <summary>Signs the document and returns the signed file. Appended as an incremental update: signatures already present stay valid.</summary>
    Task<byte[]> SignAsync(byte[] pdf, SignRequest request, CancellationToken ct = default);

    /// <summary>Names of the signature fields still waiting for a signature.</summary>
    Task<IReadOnlyList<string>> ListEmptySignatureFieldsAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>Checks every filled signature field: integrity, coverage, certificate trust, time-stamp.</summary>
    Task<IReadOnlyList<SignatureInfo>> VerifyAsync(byte[] pdf, CancellationToken ct = default);
}
