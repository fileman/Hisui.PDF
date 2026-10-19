using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Abstractions;

/// <summary>
/// Replaces a word's visual appearance by covering it with a white box and repainting new text
/// at the same position. Not a content-stream edit — font may differ slightly from the original.
/// </summary>
public interface IPdfTextEditService
{
    Task<byte[]> ReplaceWordAsync(
        byte[] pdf,
        int pageIndex,
        PdfRect wordBounds,
        string newText,
        double fontSizePoints,
        CancellationToken ct = default);

    /// <summary>
    /// Applies find-and-replace edits (see <see cref="IPdfTextExtractor.PlanReplacementsAsync"/>) in one pass:
    /// each edit's boxes are covered and its new text retyped on the first box's baseline.
    /// </summary>
    Task<byte[]> ApplyReplacementsAsync(
        byte[] pdf,
        IReadOnlyList<TextReplacement> replacements,
        CancellationToken ct = default);
}
