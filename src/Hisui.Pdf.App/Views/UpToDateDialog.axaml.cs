using Avalonia.Controls;
using Hisui.Pdf.App.Localization;

namespace Hisui.Pdf.App.Views;

public partial class UpToDateDialog : Window
{
    public UpToDateDialog() : this(new Version(0, 0, 0)) { }

    public UpToDateDialog(Version current)
    {
        InitializeComponent();

        this.FindControl<TextBlock>("MessageText")!.Text =
            Localizer.Instance.Format("UpToDate.Message", current.ToString(3));
        this.FindControl<Button>("OkButton")!.Click += (_, _) => Close();
    }
}
