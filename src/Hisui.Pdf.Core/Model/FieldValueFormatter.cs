using System.Globalization;

namespace Hisui.Pdf.Core.Model;

/// <summary>
/// Value handling for formatted text fields. The stored value (/V) is the raw value the AcroForm AF* format
/// scripts expect ("1234.5" for numbers, a fraction for percent, the formatted text for dates); the display is
/// European style ("€ 1.234,50", "25,00%").
/// </summary>
public static class FieldValueFormatter
{
    private static readonly CultureInfo Display = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>How <paramref name="raw"/> is shown. Unparseable values are shown as they are.</summary>
    public static string ToDisplay(FieldFormat format, int decimals, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (format is FieldFormat.None or FieldFormat.Date) return raw;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return raw;
        var text = (format == FieldFormat.Percent ? n * 100 : n).ToString("N" + Math.Clamp(decimals, 0, 10), Display);
        return format switch { FieldFormat.Currency => "€ " + text, FieldFormat.Percent => text + "%", _ => text };
    }

    /// <summary>Turns typed text into the value to store; null when it does not fit the format.</summary>
    public static string? ToRaw(FieldFormat format, string datePattern, string input)
    {
        var text = input.Trim();
        if (text.Length == 0 || format == FieldFormat.None) return format == FieldFormat.None ? input : "";
        if (format == FieldFormat.Date)
        {
            var net = ToNetPattern(datePattern);
            return DateTime.TryParseExact(text, net, Display, DateTimeStyles.AllowWhiteSpaces, out var d) ||
                   DateTime.TryParse(text, Display, DateTimeStyles.AllowWhiteSpaces, out d)
                ? d.ToString(net, Display)
                : null;
        }
        text = text.Replace("€", "").Replace("%", "").Replace(" ", "").Replace(" ", "");
        // "1.234,5" (it) or "1234.5" (invariant): a comma means the European style.
        var ok = text.Contains(',')
            ? double.TryParse(text, NumberStyles.Number, Display, out var n)
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out n);
        if (!ok) return null;
        if (format == FieldFormat.Percent) n /= 100;
        return n.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>AcroForm date pattern (m = month, M = minute) → .NET (M = month, m = minute).</summary>
    public static string ToNetPattern(string pattern) =>
        new(pattern.Select(c => c switch { 'm' => 'M', 'M' => 'm', 'Y' => 'y', 'D' => 'd', _ => c }).ToArray());
}
