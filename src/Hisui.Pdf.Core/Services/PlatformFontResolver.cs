using PdfSharp.Fonts;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Resolves fonts from the platform's system font directories for PDFsharp 6.x.
/// Covers the common subset (Arial / Calibri / Times New Roman / Courier New) used by
/// annotation operations. On Linux, transparently substitutes Liberation Sans / DejaVu
/// when the Windows-only faces are not installed.
/// </summary>
internal sealed class SystemFontResolver : IFontResolver
{
    private static readonly string[] FontDirectories = BuildFontDirectories();

    // Face-name → ordered candidate file names (first match wins).
    private static readonly (string Face, string[] Candidates)[] Faces =
    [
        ("Arial",              ["arial.ttf",    "Arial.ttf",             "LiberationSans-Regular.ttf",    "DejaVuSans.ttf"]),
        ("Arial#Bold",         ["arialbd.ttf",  "Arial Bold.ttf",        "LiberationSans-Bold.ttf",       "DejaVuSans-Bold.ttf"]),
        ("Arial#Italic",       ["ariali.ttf",   "Arial Italic.ttf",      "LiberationSans-Italic.ttf",     "DejaVuSans-Oblique.ttf"]),
        ("Arial#BoldItalic",   ["arialbi.ttf",  "Arial Bold Italic.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf"]),
        ("Calibri",            ["calibri.ttf",  "LiberationSans-Regular.ttf",    "DejaVuSans.ttf"]),
        ("Calibri#Bold",       ["calibrib.ttf", "LiberationSans-Bold.ttf",       "DejaVuSans-Bold.ttf"]),
        ("Calibri#Italic",     ["calibrii.ttf", "LiberationSans-Italic.ttf",     "DejaVuSans-Oblique.ttf"]),
        ("Calibri#BoldItalic", ["calibriz.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf"]),
        ("Times New Roman",    ["times.ttf",    "Times New Roman.ttf",   "LiberationSerif-Regular.ttf", "DejaVuSerif.ttf"]),
        ("Courier New",        ["cour.ttf",     "Courier New.ttf",       "LiberationMono-Regular.ttf",  "DejaVuSansMono.ttf"]),
    ];

    // Cache: face-name → resolved full path (null = not found after exhaustive search).
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string DefaultFaceName => "Arial";

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var suffix = (isBold, isItalic) switch
        {
            (true, true)  => "#BoldItalic",
            (true, false) => "#Bold",
            (false, true) => "#Italic",
            _             => string.Empty,
        };

        var key = familyName + suffix;
        if (Array.Exists(Faces, f => string.Equals(f.Face, key, StringComparison.OrdinalIgnoreCase)))
            return new FontResolverInfo(key);
        if (Array.Exists(Faces, f => string.Equals(f.Face, familyName, StringComparison.OrdinalIgnoreCase)))
            return new FontResolverInfo(familyName);
        return new FontResolverInfo(DefaultFaceName);
    }

    public byte[]? GetFont(string faceName)
    {
        var path = Resolve(faceName);
        return path is not null ? File.ReadAllBytes(path) : null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string? Resolve(string faceName)
    {
        if (_cache.TryGetValue(faceName, out var cached)) return cached;

        var entry = Array.Find(Faces, f => string.Equals(f.Face, faceName, StringComparison.OrdinalIgnoreCase));
        string? found = null;

        if (entry.Candidates is not null)
        {
            foreach (var candidate in entry.Candidates)
            {
                found = FindInDirectories(candidate);
                if (found is not null) break;
            }
        }

        _cache[faceName] = found;
        return found;
    }

    private static string? FindInDirectories(string fileName)
    {
        foreach (var dir in FontDirectories)
        {
            if (!Directory.Exists(dir)) continue;

            var direct = Path.Combine(dir, fileName);
            if (File.Exists(direct)) return direct;

            // Recurse: Linux fonts often live in subdirectories (e.g. truetype/liberation/).
            try
            {
                var hit = Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories)
                                   .FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return null;
    }

    private static string[] BuildFontDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts")];
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "/System/Library/Fonts",
                "/Library/Fonts",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Fonts"),
            ];
        }

        // Linux / BSD
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            "/usr/share/fonts",
            "/usr/local/share/fonts",
            Path.Combine(home, ".fonts"),
            Path.Combine(home, ".local/share/fonts"),
        ];
    }
}
