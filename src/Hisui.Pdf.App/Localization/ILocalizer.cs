using System.ComponentModel;
using System.Globalization;

namespace Hisui.Pdf.App.Localization;

/// <summary>
/// Supplies localized UI strings and lets the language change at runtime. Implements
/// <see cref="INotifyPropertyChanged"/> so XAML <c>{loc:Tr key}</c> bindings (which bind to the
/// string indexer) refresh when the language changes.
/// </summary>
public interface ILocalizer : INotifyPropertyChanged
{
    /// <summary>The translation for <paramref name="key"/> in the current language, falling back to
    /// the base language and finally to the key itself when missing.</summary>
    string this[string key] { get; }

    /// <summary>Looks up <paramref name="key"/> and formats it with <paramref name="args"/> using the
    /// invariant culture (numbers/dates stay stable across UI languages).</summary>
    string Format(string key, params object[] args);

    /// <summary>Current language code (e.g. "it", "en").</summary>
    string Language { get; }

    /// <summary>Culture matching <see cref="Language"/>.</summary>
    CultureInfo Culture { get; }

    /// <summary>Languages offered in the UI selector.</summary>
    IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    /// <summary>Switches the active language and notifies bindings. No-op if already active.</summary>
    void SetLanguage(string language);
}
