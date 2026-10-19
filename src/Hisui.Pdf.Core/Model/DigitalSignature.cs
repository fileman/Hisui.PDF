using System.Security.Cryptography.X509Certificates;

namespace Hisui.Pdf.Core.Model;

/// <summary>What to sign with and how the signature looks.</summary>
public sealed record SignRequest
{
    /// <summary>Certificate with its private key (Windows store, smart card or a .pfx file).</summary>
    public required X509Certificate2 Certificate { get; init; }
    public int PageIndex { get; init; }
    /// <summary>Existing empty signature field to sign into; its widget gives page and box (PageIndex and Rect are ignored).</summary>
    public string? FieldName { get; init; }
    /// <summary>Visible signature box (normalised, rendered orientation); null = invisible signature.</summary>
    public PdfRect? Rect { get; init; }
    public string? Reason { get; init; }
    public string? Location { get; init; }
    public string? ContactInfo { get; init; }
    /// <summary>RFC 3161 time-stamp server (PAdES-B-T); null = no time-stamp (PAdES-B-B).</summary>
    public Uri? TimestampServer { get; init; }
    /// <summary>Optional handwritten-signature image (PNG/JPEG) drawn in the visible box.</summary>
    public byte[]? Image { get; init; }
}

/// <summary>Result of checking one signature of a document.</summary>
public sealed record SignatureInfo
{
    public required string FieldName { get; init; }
    public string SignerName { get; init; } = "";
    public string? Issuer { get; init; }
    /// <summary>From the time-stamp when present, else the time the signer's computer declared (/M).</summary>
    public DateTime? SigningTime { get; init; }
    public bool HasTimestamp { get; init; }
    public string? Reason { get; init; }
    public string? Location { get; init; }
    public string SubFilter { get; init; } = "";
    /// <summary>The signed bytes are unchanged and the cryptographic signature is valid.</summary>
    public bool IntegrityOk { get; init; }
    /// <summary>Nothing was appended after this signature (false: later incremental updates exist).</summary>
    public bool CoversWholeDocument { get; init; }
    /// <summary>The certificate chains to a root trusted by this computer.</summary>
    public bool CertificateTrusted { get; init; }
    /// <summary>Chain problems, or the reason the signature could not be checked.</summary>
    public string? Problem { get; init; }
    public DateTime? CertificateNotAfter { get; init; }
}
