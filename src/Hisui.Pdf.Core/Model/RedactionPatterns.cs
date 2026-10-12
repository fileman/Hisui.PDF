using System.Text.RegularExpressions;

namespace Hisui.Pdf.Core.Model;

/// <summary>Ready-made patterns for "find and redact" (Italian identifiers first).</summary>
public static class RedactionPatterns
{
    public static readonly IReadOnlyDictionary<string, Regex> Builtin = new Dictionary<string, Regex>
    {
        // 16-char Italian tax code; omocodia letters (LMNPQRSTUV) allowed in the digit positions.
        ["CodiceFiscale"] = new(@"\b[A-Z]{6}[0-9LMNPQRSTUV]{2}[ABCDEHLMPRST][0-9LMNPQRSTUV]{2}[A-Z][0-9LMNPQRSTUV]{3}[A-Z]\b", RegexOptions.IgnoreCase),
        // IBAN, compact or in groups of four ("IT60 X054 2811 1010 0000 0123 456").
        ["Iban"] = new(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b", RegexOptions.IgnoreCase),
        ["PartitaIva"] = new(@"\b(?:IT)?\d{11}\b", RegexOptions.IgnoreCase),
        ["Email"] = new(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase),
        ["Telefono"] = new(@"(?<![\d])(?:\+39 ?)?(?:3\d{2}|0\d{1,3})[ .]?\d{3,4}[ .]?\d{3,4}(?![\d])"),
        ["Data"] = new(@"\b\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}\b"),
    };
}
