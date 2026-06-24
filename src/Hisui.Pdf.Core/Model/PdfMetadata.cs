namespace Hisui.Pdf.Core.Model;

/// <summary>Snapshot of the Info dictionary fields of a PDF document.</summary>
public sealed record PdfMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
}
