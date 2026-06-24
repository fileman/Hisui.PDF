using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Hisui.Pdf.App.Converters;

/// <summary>
/// Bidirectional converter for binding RadioButton.IsChecked to an enum property.
/// ConverterParameter must be the string name of the enum member (e.g. "Highlight").
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name)
            return Enum.Parse(targetType, name);
        return BindingOperations.DoNothing;
    }
}
