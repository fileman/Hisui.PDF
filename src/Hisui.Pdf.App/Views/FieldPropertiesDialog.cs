using Avalonia.Controls;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

/// <summary>Form field properties (name, tooltip, flags, font, alignment, format, options), built on <see cref="FormDialog"/>.</summary>
internal static class FieldPropertiesDialog
{
    /// <param name="count">Selected fields: above 1 name and description are hidden and changes go to all.</param>
    public static async Task<FormFieldProperties?> ShowAsync(Window owner, FormFieldProperties p, int count = 1)
    {
        var loc = Localizer.Instance;
        var multi = count > 1;
        var dialog = new FormDialog(multi ? loc.Format("FieldProps.TitleMulti", count) : loc.Format("FieldProps.Title", p.Name), 440);
        var isText = p.Type == PdfFormFieldType.Text;
        var isChoice = p.Type is PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox;

        var name = new TextBox { Text = p.Name };
        if (!multi) dialog.Add(loc["FieldProps.Name"], name);
        // Name and description belong to one field: hidden when editing several (each keeps its own).
        var tooltip = new TextBox { Text = p.Tooltip ?? "" };
        if (!multi) dialog.Add(loc["FieldProps.Tooltip"], tooltip);
        var required = dialog.Add(null, new CheckBox { Content = loc["FieldProps.Required"], IsChecked = p.Required });
        var readOnly = dialog.Add(null, new CheckBox { Content = loc["FieldProps.ReadOnly"], IsChecked = p.ReadOnly });

        CheckBox? multiline = null;
        NumericUpDown? maxLen = null, fontSize = null, decimals = null;
        ComboBox? font = null, align = null, format = null;
        TextBox? datePattern = null, options = null;
        if (isText || isChoice)
        {
            font = dialog.Add(loc["FieldProps.Font"], new ComboBox
            {
                ItemsSource = new[] { "Helvetica", "Times", "Courier" },
                SelectedIndex = p.FontName switch { "TiRo" => 1, "Cour" => 2, _ => 0 },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            });
            fontSize = dialog.Add(loc["FieldProps.FontSize"], new NumericUpDown { Value = (decimal)p.FontSize, Minimum = 0, Maximum = 72, Increment = 1, FormatString = "0.#" });
            align = dialog.Add(loc["FieldProps.Alignment"], new ComboBox
            {
                ItemsSource = new[] { loc["FieldProps.Left"], loc["FieldProps.Center"], loc["FieldProps.Right"] },
                SelectedIndex = (int)p.Alignment,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            });
        }
        if (isText)
        {
            multiline = dialog.Add(null, new CheckBox { Content = loc["FieldProps.Multiline"], IsChecked = p.Multiline });
            maxLen = dialog.Add(loc["FieldProps.MaxLength"], new NumericUpDown { Value = p.MaxLength ?? 0, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0" });
            format = dialog.Add(loc["FieldProps.Format"], new ComboBox
            {
                ItemsSource = new[] { loc["FieldProps.FormatNone"], loc["FieldProps.FormatNumber"], loc["FieldProps.FormatCurrency"], loc["FieldProps.FormatPercent"], loc["FieldProps.FormatDate"] },
                SelectedIndex = (int)p.Format,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            });
            decimals = dialog.Add(loc["FieldProps.Decimals"], new NumericUpDown { Value = p.Decimals, Minimum = 0, Maximum = 10, Increment = 1, FormatString = "0" });
            datePattern = dialog.Add(loc["FieldProps.DatePattern"], new TextBox { Text = p.DatePattern });
        }
        if (isChoice)
            options = dialog.Add(loc["FieldProps.Options"], new TextBox { Text = string.Join('\n', p.Options), AcceptsReturn = true, Height = 110, TextWrapping = Avalonia.Media.TextWrapping.NoWrap });

        if (!await dialog.ShowAsync(owner, validate: () => !string.IsNullOrWhiteSpace(name.Text))) return null;

        return p with
        {
            Name = name.Text!.Trim(),
            Tooltip = string.IsNullOrWhiteSpace(tooltip.Text) ? null : tooltip.Text.Trim(),
            Required = required.IsChecked == true,
            ReadOnly = readOnly.IsChecked == true,
            Multiline = multiline?.IsChecked == true,
            MaxLength = maxLen?.Value is { } m && m > 0 ? (int)m : null,
            FontName = font?.SelectedIndex switch { 1 => "TiRo", 2 => "Cour", _ => "Helv" },
            FontSize = (double)(fontSize?.Value ?? 0),
            Alignment = (FieldAlignment)Math.Max(0, align?.SelectedIndex ?? 0),
            Format = (FieldFormat)Math.Max(0, format?.SelectedIndex ?? 0),
            Decimals = (int)(decimals?.Value ?? p.Decimals),
            DatePattern = string.IsNullOrWhiteSpace(datePattern?.Text) ? p.DatePattern : datePattern.Text.Trim(),
            Options = options is null ? p.Options : options.Text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
        };
    }
}
