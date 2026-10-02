using Avalonia.Controls;
using Hisui.Pdf.App.Localization;

namespace Hisui.Pdf.App.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var version = typeof(AboutDialog).Assembly.GetName().Version;
        var versionText = this.FindControl<TextBlock>("VersionText");
        if (versionText is not null)
            versionText.Text = Localizer.Instance.Format("About.Version", version?.ToString(3) ?? "1.0.0");

        var closeButton = this.FindControl<Button>("CloseButton");
        if (closeButton is not null)
            closeButton.Click += (_, _) => Close();

        this.WireAcceptCancel(() => Close(), () => Close()); // Enter or Esc both dismiss the About box
    }
}
