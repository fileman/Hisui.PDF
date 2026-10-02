using Avalonia.Controls;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.Services;

namespace Hisui.Pdf.App.Views;

public enum UpdateChoice { Later, Skip, Download }

public partial class UpdateDialog : Window
{
    public UpdateDialog() : this(new UpdateInfo(new Version(0, 0, 0), "v0.0.0", "", null), new Version(0, 0, 0)) { }

    public UpdateDialog(UpdateInfo update, Version current)
    {
        InitializeComponent();

        this.FindControl<TextBlock>("MessageText")!.Text =
            Localizer.Instance.Format("Update.Message", update.Version.ToString(3), current.ToString(3));

        var notes = this.FindControl<TextBlock>("NotesText")!;
        notes.Text = update.Notes is { Length: > 0 } n ? (n.Length > 600 ? n[..600] + "…" : n) : "";
        notes.IsVisible = notes.Text.Length > 0;

        this.FindControl<Button>("DownloadButton")!.Click += (_, _) => Close(UpdateChoice.Download);
        this.FindControl<Button>("SkipButton")!.Click += (_, _) => Close(UpdateChoice.Skip);
        this.FindControl<Button>("LaterButton")!.Click += (_, _) => Close(UpdateChoice.Later);
    }
}
