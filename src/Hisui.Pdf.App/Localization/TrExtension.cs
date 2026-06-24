using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Hisui.Pdf.App.Localization;

/// <summary>
/// XAML markup extension: <c>{loc:Tr Some.Key}</c> binds a property to the localized string for
/// <c>Some.Key</c>. It returns a one-way <see cref="Binding"/> to the <see cref="Localizer"/> string
/// indexer, so when the language changes the binding re-reads and the UI updates without a reload.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key,
        };
}
