using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Platform;

namespace Hisui.Pdf.App.Localization;

/// <summary>
/// JSON-backed localization. Dictionaries live as Avalonia resources under <c>Assets/i18n/{lang}.json</c>.
/// The base language (<see cref="BaseLanguage"/>) is always loaded as a fallback so a key missing from a
/// translation still shows meaningful text. A process-wide singleton (<see cref="Instance"/>) is exposed
/// so the <c>{loc:Tr}</c> markup extension — which has no access to DI — can reach it; the same instance
/// is registered in the container for constructor injection into view models.
/// </summary>
public sealed class Localizer : ILocalizer
{
    public const string BaseLanguage = "it";
    private const string ResourceUriFormat = "avares://Hisui.Pdf.App/Assets/i18n/{0}.json";

    public static Localizer Instance { get; } = new();

    private readonly IReadOnlyDictionary<string, string> _base;
    private IReadOnlyDictionary<string, string> _current;
    private string _language;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; } =
    [
        new("it", "Italiano"),
        new("en", "English"),
    ];

    private Localizer()
    {
        _base = Load(BaseLanguage);
        _current = _base;
        _language = BaseLanguage;
    }

    public string Language => _language;

    public CultureInfo Culture =>
        CultureInfo.GetCultureInfo(_language);

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (_current.TryGetValue(key, out var v)) return v;
            if (_base.TryGetValue(key, out var b)) return b;
            return key; // surface the missing key rather than blank text
        }
    }

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, this[key], args);

    public void SetLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) language = BaseLanguage;
        // Clamp unknown/tampered codes to the base language so _language is always supported (keeps
        // Culture total and stops a bad persisted value from round-tripping back to settings).
        if (!AvailableLanguages.Any(l => string.Equals(l.Code, language, StringComparison.OrdinalIgnoreCase)))
            language = BaseLanguage;
        if (string.Equals(language, _language, StringComparison.OrdinalIgnoreCase)) return;

        _current = string.Equals(language, BaseLanguage, StringComparison.OrdinalIgnoreCase)
            ? _base
            : Load(language);
        _language = language;

        // {loc:Tr key} binds to this[key], an indexer. Avalonia's indexer binding only re-reads when the
        // PropertyChanged name resolves to an indexer property — i.e. the CLR reflected name "Item" (NOT
        // an empty name, and NOT the WPF "Item[]" idiom). The extra empty-name raise covers any plain
        // scalar binding/subscriber (e.g. the view model refreshing its computed labels).
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        try
        {
            var uri = new Uri(string.Format(CultureInfo.InvariantCulture, ResourceUriFormat, language));
            using var stream = AssetLoader.Open(uri);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            // Missing/invalid resource: fall back to an empty map (callers then fall back to base/key).
            return new Dictionary<string, string>();
        }
    }
}
