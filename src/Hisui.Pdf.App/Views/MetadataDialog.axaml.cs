using Avalonia.Controls;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// Edits the document Info dictionary (title / author / subject / keywords / creator). The edited
/// values are exposed via <see cref="Result"/> when the dialog closes with true.
/// </summary>
public partial class MetadataDialog : Window
{
    public PdfMetadata? Result { get; private set; }

    public MetadataDialog() : this(new PdfMetadata()) { }

    public MetadataDialog(PdfMetadata initial)
    {
        InitializeComponent();

        Title = Localizer.Instance["Metadata.Title"];
        TitleBox.Text = initial.Title ?? string.Empty;
        AuthorBox.Text = initial.Author ?? string.Empty;
        SubjectBox.Text = initial.Subject ?? string.Empty;
        KeywordsBox.Text = initial.Keywords ?? string.Empty;
        CreatorBox.Text = initial.Creator ?? string.Empty;

        OkButton.Click += (_, _) => OnOk();
        CancelButton.Click += (_, _) => Close(false);
        this.WireAcceptCancel(OnOk, () => Close(false));
        Opened += (_, _) => TitleBox.Focus();
    }

    private void OnOk()
    {
        Result = new PdfMetadata
        {
            Title = Trim(TitleBox.Text),
            Author = Trim(AuthorBox.Text),
            Subject = Trim(SubjectBox.Text),
            Keywords = Trim(KeywordsBox.Text),
            Creator = Trim(CreatorBox.Text),
        };
        Close(true);
    }

    // Non-null so the value is written (a null would leave the existing value unchanged in WriteAsync).
    private static string Trim(string? value) => (value ?? string.Empty).Trim();
}
