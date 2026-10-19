namespace Hisui.Pdf.Core.Model;

/// <summary>
/// One find-and-replace edit on a page: the boxes of the words a match (or several matches sharing a word)
/// overlaps, and the text those words read after the replacement. Applied by removing the words' glyphs
/// from the content stream (covering the boxes where that is not possible) and retyping
/// <see cref="NewText"/> on the first box's baseline.
/// </summary>
public sealed class TextReplacement
{
    public int PageIndex { get; init; }

    /// <summary>Normalized boxes of the whole words touched, in content order; the first anchors the new text.</summary>
    public required IReadOnlyList<PdfRect> Covers { get; init; }

    /// <summary>
    /// The touched words with the match(es) replaced, e.g. "concatenate" with "cat"→"dog" gives "condogenate".
    /// Empty when the replacement deletes the words entirely.
    /// </summary>
    public required string NewText { get; init; }

    /// <summary>The touched words as they read before the replacement, space-joined.</summary>
    public string OriginalText { get; init; } = "";

    /// <summary>Approximate font size of the first touched word, in points.</summary>
    public double FontSizePoints { get; init; } = 12;

    /// <summary>Fill colour of the first touched word (0xAARRGGBB), so the retyped text matches it.</summary>
    public uint ColorArgb { get; init; } = 0xFF000000;

    /// <summary>Whether the first touched word is bold.</summary>
    public bool IsBold { get; init; }
}
