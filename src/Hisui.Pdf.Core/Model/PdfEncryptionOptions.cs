namespace Hisui.Pdf.Core.Model;

/// <summary>Options for AES-256 encryption of a PDF document.</summary>
public sealed record PdfEncryptionOptions
{
    public string UserPassword { get; init; } = "";
    public string OwnerPassword { get; init; } = "";
    public bool PermitPrint { get; init; } = true;
    public bool PermitAnnotations { get; init; } = true;
    public bool PermitFormsFill { get; init; } = true;
    public bool PermitExtractContent { get; init; } = false;
    public bool PermitModifyDocument { get; init; } = false;
    public bool PermitAssembleDocument { get; init; } = false;
}
