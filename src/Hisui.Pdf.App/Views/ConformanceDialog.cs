using System.Text;
using Avalonia.Controls;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

/// <summary>Profile picker with a live preflight report; returns the profile and options to convert to (null = cancelled).</summary>
internal static class ConformanceDialog
{
    public static async Task<(PdfProfile Profile, ConversionOptions Options)?> ShowAsync(
        Window owner, PdfProfile initial, string? title, Func<PdfProfile, Task<ConformanceReport>> check)
    {
        var loc = Localizer.Instance;
        var profiles = Enum.GetValues<PdfProfile>();
        var dialog = new FormDialog(loc["Conf.Title"], 600);
        var pick = dialog.Add(loc["Conf.Profile"], new ComboBox
        {
            ItemsSource = profiles.Select(p => $"{p.DisplayName()} — {loc["Conf.Profile." + p]}").ToList(),
            SelectedIndex = Array.IndexOf(profiles, initial),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        });
        var titleBox = dialog.Add(loc["Conf.DocTitle"], new TextBox { Text = title ?? "" });
        var lang = dialog.Add(loc["Conf.Language"], new TextBox { Text = System.Globalization.CultureInfo.CurrentUICulture.Name is { Length: > 0 } c ? c : "it-IT" });
        var report = dialog.Add(loc["Conf.Report"], new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 300, FontSize = 12,
        });
        dialog.AddText(loc["Conf.Disclaimer"], hint: true);

        var run = 0;
        async Task Refresh()
        {
            var mine = ++run;
            var profile = profiles[Math.Max(0, pick.SelectedIndex)];
            report.Text = loc["Conf.Checking"];
            try
            {
                var result = await check(profile);
                if (mine == run) report.Text = Format(result);
            }
            catch (Exception ex)
            {
                if (mine == run) report.Text = ex.Message;
            }
        }
        pick.SelectionChanged += async (_, _) => await Refresh();
        dialog.Opened += async (_, _) => await Refresh();

        if (!await dialog.ShowAsync(owner, loc["Conf.Convert"])) return null;
        return (profiles[Math.Max(0, pick.SelectedIndex)],
                new ConversionOptions { Title = titleBox.Text, Language = string.IsNullOrWhiteSpace(lang.Text) ? "it-IT" : lang.Text.Trim() });
    }

    public static string Format(ConformanceReport report)
    {
        var loc = Localizer.Instance;
        var sb = new StringBuilder();
        sb.AppendLine(report.Passes
            ? loc.Format("Conf.Passes", report.Profile.DisplayName())
            : loc.Format("Conf.Fails", report.Profile.DisplayName(), report.Issues.Count(i => i.Severity == IssueSeverity.Error)));
        if (report.Issues.Count > 0) sb.AppendLine();
        foreach (var group in report.Issues.GroupBy(i => (i.Severity, i.Code, i.Fixable)).OrderBy(g => g.Key.Severity))
        {
            var mark = group.Key.Severity switch { IssueSeverity.Error => "✖", IssueSeverity.Warning => "⚠", _ => "ℹ" };
            var details = group.Select(i => i.Detail).Where(d => d.Length > 0).Distinct().ToList();
            sb.Append(mark).Append(' ').Append(loc["Conf." + group.Key.Code]);
            sb.Append(group.Key.Fixable ? "  [" + loc["Conf.Fixable"] + "]" : "  [" + loc["Conf.Manual"] + "]");
            if (details.Count > 0) sb.Append(": ").Append(string.Join(", ", details.Take(12))).Append(details.Count > 12 ? $" (+{details.Count - 12})" : "");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
