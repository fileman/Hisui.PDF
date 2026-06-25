namespace Hisui.Pdf.Core.Model;

/// <summary>Options controlling how a text search matches a query against page text.</summary>
public sealed record TextSearchOptions
{
    /// <summary>Match case exactly when true; case-insensitive (the default) when false.</summary>
    public bool MatchCase { get; init; }

    /// <summary>Require word-boundary delimiters when true, so partial-word hits (e.g. "cat" in "category") are skipped.</summary>
    public bool WholeWord { get; init; }
}
