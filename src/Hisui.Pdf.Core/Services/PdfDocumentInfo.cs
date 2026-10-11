using PdfSharp.Pdf;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Document-information helpers shared by every service that writes a PDF: names this application as the
/// producer and carries the descriptive metadata over when a document is rebuilt from imported pages.
/// </summary>
internal static class PdfDocumentInfo
{
    // Same X.Y.Z as the app (the release build stamps one version on every project).
    private static readonly string ProducerName = BuildProducerName();

    private static string BuildProducerName()
    {
        var v = typeof(PdfDocumentInfo).Assembly.GetName().Version;
        return v is null ? "Hisui PDF" : $"Hisui PDF {v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }

    /// <summary>
    /// Saves with this application as /Producer. PDFsharp always prefixes its own name, giving
    /// "PDFsharp x.y.z (Original: Hisui PDF x.y.z)"; that prefix is added by the library, not required by its MIT license.
    /// </summary>
    public static void SaveStamped(this PdfDocument doc, Stream stream)
    {
        doc.Info.Elements.SetString("/Producer", ProducerName);
        doc.Save(stream);
    }

    /// <summary>Copies title, author, subject, keywords and creator from <paramref name="source"/> (a rebuilt document starts empty).</summary>
    public static void CopyFrom(this PdfDocumentInformation target, PdfDocumentInformation source)
    {
        if (source.Title.Length > 0) target.Title = source.Title;
        if (source.Author.Length > 0) target.Author = source.Author;
        if (source.Subject.Length > 0) target.Subject = source.Subject;
        if (source.Keywords.Length > 0) target.Keywords = source.Keywords;
        if (source.Creator.Length > 0) target.Creator = source.Creator;
    }
}
