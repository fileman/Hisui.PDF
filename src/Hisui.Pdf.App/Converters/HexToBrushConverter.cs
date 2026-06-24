using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Hisui.Pdf.App.Converters;

/// <summary>Converts a CSS-style hex string (e.g. "#FFCC00") to a brush for a live color swatch.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            var hex = s.Trim();
            if (!hex.StartsWith('#')) hex = "#" + hex;
            if (Color.TryParse(hex, out var color))
                return new SolidColorBrush(color);
        }
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
