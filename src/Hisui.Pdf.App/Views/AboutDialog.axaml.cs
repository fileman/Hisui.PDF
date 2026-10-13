using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Hisui.Pdf.App.Localization;

namespace Hisui.Pdf.App.Views;

public partial class AboutDialog : Window
{
    private const string RepoUrl = "https://github.com/fileman/Hisui.PDF";

    public AboutDialog()
    {
        InitializeComponent();

        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var versionText = this.FindControl<TextBlock>("VersionText");
        if (versionText is not null)
            versionText.Text = Localizer.Instance.Format("About.Version", version);

        var closeButton = this.FindControl<Button>("CloseButton");
        if (closeButton is not null)
            closeButton.Click += (_, _) => Close();

        // New GitHub issue with version + OS prefilled so reports arrive with the basics.
        var reportButton = this.FindControl<Button>("ReportButton");
        if (reportButton is not null)
            reportButton.Click += (_, _) =>
            {
                var body = Uri.EscapeDataString($"Version: {version}\nOS: {RuntimeInformation.OSDescription}\n\n");
                try { Process.Start(new ProcessStartInfo($"{RepoUrl}/issues/new?body={body}") { UseShellExecute = true }); }
                catch { /* no browser registered: nothing useful to do */ }
            };

        var shareButton = this.FindControl<Button>("ShareButton");
        if (shareButton is not null)
            shareButton.Click += async (_, _) =>
            {
                if (Clipboard is not { } cb) return;
                await cb.SetTextAsync(Localizer.Instance.Format("About.ShareText", $"{RepoUrl}/releases/latest"));
                shareButton.Content = Localizer.Instance["About.LinkCopied"];
            };

        this.WireAcceptCancel(() => Close(), () => Close()); // Enter or Esc both dismiss the About box
    }
}
