using System.Net.Http;
using Hisui.Pdf.Core.Abstractions;
using Tesseract;

namespace Hisui.Pdf.Core.Services;

/// <summary>An OCR language: <see cref="Bundled"/> ships with the app, <see cref="Installed"/> is usable now.</summary>
public sealed record OcrLanguage(string Code, string Name, bool Bundled, bool Installed);

/// <summary>
/// OCR language data. The app ships Italian and English; any other language is fetched only when the user asks for it
/// (from the official tesseract-ocr/tessdata_fast repository, pinned to a release tag) or imported from a local
/// <c>.traineddata</c> file — nothing is downloaded automatically. Extra languages live in the user's data folder.
/// </summary>
public sealed class OcrLanguageStore : IOcrLanguageStore
{
    /// <summary>Same model family as the bundled files ("fast" integer models), at a fixed release.</summary>
    public const string SourceTemplate = "https://github.com/tesseract-ocr/tessdata_fast/raw/4.1.0/{0}.traineddata";

    /// <summary>Codes offered for download, with their native names.</summary>
    public static readonly IReadOnlyDictionary<string, string> Catalog = new Dictionary<string, string>
    {
        ["ita"] = "Italiano", ["eng"] = "English", ["deu"] = "Deutsch", ["fra"] = "Français", ["spa"] = "Español",
        ["por"] = "Português", ["nld"] = "Nederlands", ["cat"] = "Català", ["glg"] = "Galego", ["eus"] = "Euskara",
        ["pol"] = "Polski", ["ces"] = "Čeština", ["slk"] = "Slovenčina", ["slv"] = "Slovenščina", ["hrv"] = "Hrvatski",
        ["srp"] = "Српски", ["bos"] = "Bosanski", ["ron"] = "Română", ["hun"] = "Magyar", ["bul"] = "Български",
        ["ell"] = "Ελληνικά", ["rus"] = "Русский", ["ukr"] = "Українська", ["swe"] = "Svenska", ["nor"] = "Norsk",
        ["dan"] = "Dansk", ["fin"] = "Suomi", ["est"] = "Eesti", ["lav"] = "Latviešu", ["lit"] = "Lietuvių",
        ["isl"] = "Íslenska", ["sqi"] = "Shqip", ["mlt"] = "Malti", ["tur"] = "Türkçe", ["lat"] = "Latina",
        ["ara"] = "العربية", ["heb"] = "עברית", ["hin"] = "हिन्दी", ["chi_sim"] = "中文 (简体)", ["chi_tra"] = "中文 (繁體)",
        ["jpn"] = "日本語", ["kor"] = "한국어", ["vie"] = "Tiếng Việt", ["tha"] = "ไทย",
    };

    private readonly string _bundledDir;
    private readonly string _userDir;
    private readonly HttpClient _http;

    public OcrLanguageStore(string bundledDir, string userDir, HttpClient http) => (_bundledDir, _userDir, _http) = (bundledDir, userDir, http);

    public IReadOnlyList<OcrLanguage> List()
    {
        var bundled = Codes(_bundledDir);
        var user = Codes(_userDir);
        return Catalog.Keys.Union(bundled).Union(user)
            .Select(code => new OcrLanguage(code, Catalog.GetValueOrDefault(code, code), bundled.Contains(code), bundled.Contains(code) || user.Contains(code)))
            .OrderByDescending(l => l.Bundled).ThenByDescending(l => l.Installed).ThenBy(l => l.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public string DataPathFor(IEnumerable<string> codes)
    {
        var wanted = codes.ToList();
        var bundled = Codes(_bundledDir);
        if (wanted.All(bundled.Contains)) return _bundledDir;
        // Tesseract takes one folder: bring the bundled languages the mix needs next to the downloaded ones.
        Directory.CreateDirectory(_userDir);
        foreach (var code in wanted.Where(bundled.Contains))
        {
            var target = Path.Combine(_userDir, code + ".traineddata");
            if (!File.Exists(target)) File.Copy(Path.Combine(_bundledDir, code + ".traineddata"), target);
        }
        return _userDir;
    }

    public Uri SourceFor(string code) => new(string.Format(SourceTemplate, Uri.EscapeDataString(code)));

    public async Task InstallAsync(string code, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Catalog.ContainsKey(code)) throw new ArgumentException($"Unknown OCR language '{code}'.", nameof(code));
        var temp = Path.Combine(Path.GetTempPath(), "hisui-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var file = Path.Combine(temp, code + ".traineddata");
            using (var response = await _http.GetAsync(SourceFor(code), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(file);
                var buffer = new byte[81_920];
                long done = 0;
                for (int n; (n = await source.ReadAsync(buffer, ct)) > 0;)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total.Value);
                }
            }
            await AdoptAsync(file, code, ct);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    public async Task<string> ImportAsync(string path, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".traineddata", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a Tesseract '.traineddata' file.", nameof(path));
        var code = name[..^".traineddata".Length];
        await AdoptAsync(path, code, ct);
        return code;
    }

    public void Remove(string code)
    {
        var file = Path.Combine(_userDir, code + ".traineddata");
        if (Codes(_bundledDir).Contains(code) && File.Exists(file)) File.Delete(file); // just a copy of a bundled file
        else if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Checks that Tesseract accepts the file, then moves/copies it into the user folder.</summary>
    private async Task AdoptAsync(string file, string code, CancellationToken ct)
    {
        if (code.Length == 0 || code.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || code.Contains(".."))
            throw new ArgumentException($"Invalid language file name '{code}'.");
        if (new FileInfo(file).Length < 50_000) throw new InvalidDataException("The language file is too small to be valid.");
        await Task.Run(() =>
        {
            var probe = Path.Combine(Path.GetTempPath(), "hisui-ocr-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probe);
            try
            {
                File.Copy(file, Path.Combine(probe, code + ".traineddata"));
                try { using var engine = new TesseractEngine(probe, code, EngineMode.Default); }
                catch (TesseractException ex) { throw new InvalidDataException("Tesseract cannot load this language file.", ex); }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException) { /* engine not installed here: keep the file */ }
                Directory.CreateDirectory(_userDir);
                File.Copy(file, Path.Combine(_userDir, code + ".traineddata"), overwrite: true);
            }
            finally
            {
                try { Directory.Delete(probe, recursive: true); } catch (IOException) { }
            }
        }, ct);
    }

    private static HashSet<string> Codes(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.traineddata").Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal)
            : [];
}
