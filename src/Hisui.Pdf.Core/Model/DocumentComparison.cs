namespace Hisui.Pdf.Core.Model;

/// <summary>Comparison result. <see cref="Marks"/> are boxes on the original's pages ("− removed", "+ added after this word").</summary>
public sealed record DocumentComparison(IReadOnlyList<TextSearchResult> Marks, IReadOnlyList<PageChange> Pages)
{
    public bool IsIdentical => Pages.Count == 0;
}

/// <summary>Text removed from / added to one page (zero-based). A page present in only one version has the other flag false.</summary>
public sealed record PageChange(int PageIndex, bool InOriginal, bool InRevised, string Removed, string Added);
