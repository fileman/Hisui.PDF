using Avalonia.Controls;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.ViewModels;

namespace Hisui.Pdf.App.Views;

/// <summary>Link target and header/footer prompts, built on <see cref="FormDialog"/>.</summary>
internal static class NavigationDialogs
{
    public static async Task<(int? Page, string? Uri)?> PromptLinkAsync(Window owner)
    {
        var loc = Localizer.Instance;
        var dialog = new FormDialog(loc["Links.Title"]);
        var toPage = dialog.Add(null, new RadioButton { Content = loc["Links.ToPage"], IsChecked = true, GroupName = "linkTarget" });
        var page = dialog.Add(null, new NumericUpDown { Value = 1, Minimum = 1, Maximum = 100000, FormatString = "0", Margin = new Avalonia.Thickness(24, 0, 0, 0) });
        var toWeb = dialog.Add(null, new RadioButton { Content = loc["Links.ToWeb"], GroupName = "linkTarget" });
        var uri = dialog.Add(null, new TextBox { PlaceholderText = "https://", Margin = new Avalonia.Thickness(24, 0, 0, 0) });
        uri.GotFocus += (_, _) => toWeb.IsChecked = true;
        page.GotFocus += (_, _) => toPage.IsChecked = true;
        var ok = await dialog.ShowAsync(owner, validate: () => toPage.IsChecked == true
            ? page.Value is > 0
            : Uri.TryCreate(uri.Text?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto");
        if (!ok) return null;
        return toPage.IsChecked == true ? ((int)page.Value!.Value, null) : (null, uri.Text!.Trim());
    }

    public static async Task ShowReportAsync(Window owner, string title, string text)
    {
        var dialog = new FormDialog(title, 640);
        dialog.Add(null, new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 420, FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono, Consolas, $Default"), FontSize = 12,
        });
        await dialog.ShowAsync(owner, "OK", showCancel: false);
    }

    public static async Task<IReadOnlyList<System.Text.RegularExpressions.Regex>?> PromptRedactionPatternsAsync(Window owner)
    {
        var loc = Localizer.Instance;
        var dialog = new FormDialog(loc["Redact.Title"]);
        dialog.AddText(loc["Redact.Intro"], hint: true);
        var checks = Hisui.Pdf.Core.Model.RedactionPatterns.Builtin.Keys
            .Select(key => (key, box: dialog.Add(null, new CheckBox { Content = loc["Redact.Pattern." + key], IsChecked = key is "CodiceFiscale" or "Iban" })))
            .ToList();
        var custom = dialog.Add(loc["Redact.Custom"], new TextBox { PlaceholderText = loc["Redact.CustomHint"] });
        System.Text.RegularExpressions.Regex? customRegex = null;
        var ok = await dialog.ShowAsync(owner, loc["Redact.Find"], () =>
        {
            if (string.IsNullOrWhiteSpace(custom.Text)) { customRegex = null; return checks.Any(c => c.box.IsChecked == true); }
            try { customRegex = new System.Text.RegularExpressions.Regex(custom.Text, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)); return true; }
            catch (ArgumentException) { return false; }
        });
        if (!ok) return null;
        var result = checks.Where(c => c.box.IsChecked == true).Select(c => Hisui.Pdf.Core.Model.RedactionPatterns.Builtin[c.key]).ToList();
        if (customRegex is not null) result.Add(customRegex);
        return result;
    }

    public static async Task<HeaderFooterOptions?> PromptHeaderFooterAsync(Window owner)
    {
        var loc = Localizer.Instance;
        var dialog = new FormDialog(loc["Stamp.Title"], 520);
        dialog.AddText(loc["Stamp.Tokens"], hint: true);
        TextBox Box(string caption, string text = "") => dialog.Add(caption, new TextBox { Text = text });
        var tl = Box(loc["Stamp.TopLeft"]);
        var tc = Box(loc["Stamp.TopCenter"]);
        var tr = Box(loc["Stamp.TopRight"]);
        var bl = Box(loc["Stamp.BottomLeft"]);
        var bc = Box(loc["Stamp.BottomCenter"], loc["Stamp.DefaultFooter"]);
        var br = Box(loc["Stamp.BottomRight"]);
        var size = dialog.Add(loc["Stamp.FontSize"], new NumericUpDown { Value = 9, Minimum = 4, Maximum = 48, FormatString = "0.#" });
        var margin = dialog.Add(loc["Stamp.Margin"], new NumericUpDown { Value = 24, Minimum = 0, Maximum = 200, FormatString = "0" });
        var prefix = Box(loc["Stamp.BatesPrefix"]);
        var start = dialog.Add(loc["Stamp.BatesStart"], new NumericUpDown { Value = 1, Minimum = 0, Maximum = 999999999, FormatString = "0" });
        var digits = dialog.Add(loc["Stamp.BatesDigits"], new NumericUpDown { Value = 6, Minimum = 1, Maximum = 12, FormatString = "0" });

        if (!await dialog.ShowAsync(owner)) return null;
        return new HeaderFooterOptions
        {
            TopLeft = tl.Text ?? "", TopCenter = tc.Text ?? "", TopRight = tr.Text ?? "",
            BottomLeft = bl.Text ?? "", BottomCenter = bc.Text ?? "", BottomRight = br.Text ?? "",
            FontSize = (double)(size.Value ?? 9), Margin = (double)(margin.Value ?? 24),
            BatesPrefix = prefix.Text ?? "", BatesStart = (int)(start.Value ?? 1), BatesDigits = (int)(digits.Value ?? 6),
        };
    }
}
