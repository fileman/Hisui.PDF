namespace Hisui.Pdf.Core.Model;

/// <summary>Target PDF version or standard profile.</summary>
public enum PdfProfile
{
    Pdf14, Pdf15, Pdf16, Pdf17, Pdf20,
    PdfA1b, PdfA2b, PdfA2u, PdfA3b, PdfA3u,
    PdfUA1,
    PdfX4,
}

public enum IssueSeverity { Error, Warning, Info }

/// <summary>
/// One finding. <see cref="Code"/> names the rule (UI text key "Conf.{Code}"); <see cref="Detail"/> is the specific
/// object (font name, page number…). <see cref="Fixable"/>: the conversion corrects it.
/// </summary>
public sealed record ConformanceIssue(IssueSeverity Severity, string Code, string Detail, bool Fixable);

public sealed record ConformanceReport(PdfProfile Profile, IReadOnlyList<ConformanceIssue> Issues)
{
    public bool Passes => Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>Values the conversion may need (PDF/UA wants a title and a language).</summary>
public sealed record ConversionOptions
{
    public string? Title { get; init; }
    public string Language { get; init; } = "it-IT";
}

public static class PdfProfiles
{
    public static bool IsPdfA(this PdfProfile p) => p is PdfProfile.PdfA1b or PdfProfile.PdfA2b or PdfProfile.PdfA2u or PdfProfile.PdfA3b or PdfProfile.PdfA3u;

    /// <summary>PDF/A part (1–3), or 0.</summary>
    public static int PdfAPart(this PdfProfile p) => p switch
    {
        PdfProfile.PdfA1b => 1, PdfProfile.PdfA2b or PdfProfile.PdfA2u => 2, PdfProfile.PdfA3b or PdfProfile.PdfA3u => 3, _ => 0,
    };

    /// <summary>PDF/A conformance level letter ("B" or "U").</summary>
    public static string PdfALevel(this PdfProfile p) => p is PdfProfile.PdfA2u or PdfProfile.PdfA3u ? "U" : "B";

    /// <summary>File version the profile requires, as PDFsharp's integer (14 = 1.4 … 20 = 2.0).</summary>
    public static int Version(this PdfProfile p) => p switch
    {
        PdfProfile.Pdf14 or PdfProfile.PdfA1b => 14,
        PdfProfile.Pdf15 => 15,
        PdfProfile.Pdf16 or PdfProfile.PdfX4 => 16,
        PdfProfile.Pdf20 => 20,
        _ => 17,
    };

    public static string DisplayName(this PdfProfile p) => p switch
    {
        PdfProfile.Pdf14 => "PDF 1.4", PdfProfile.Pdf15 => "PDF 1.5", PdfProfile.Pdf16 => "PDF 1.6", PdfProfile.Pdf17 => "PDF 1.7", PdfProfile.Pdf20 => "PDF 2.0",
        PdfProfile.PdfA1b => "PDF/A-1b", PdfProfile.PdfA2b => "PDF/A-2b", PdfProfile.PdfA2u => "PDF/A-2u",
        PdfProfile.PdfA3b => "PDF/A-3b", PdfProfile.PdfA3u => "PDF/A-3u", PdfProfile.PdfUA1 => "PDF/UA-1", _ => "PDF/X-4",
    };
}
