using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Hisui.Pdf.App.Localization;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// Captures a new signature in one of three ways — freehand drawing, an image file, or typed cursive
/// text — and rasterizes the result to a transparent PNG. The PNG bytes and the chosen name are
/// exposed via <see cref="ResultPng"/> / <see cref="ResultName"/> when the dialog closes with true.
/// </summary>
public partial class SignatureCreateDialog : Window
{
    private byte[]? _imageBytes;

    public byte[]? ResultPng { get; private set; }
    public string ResultName { get; private set; } = string.Empty;

    public SignatureCreateDialog()
    {
        InitializeComponent();

        DrawMode.IsCheckedChanged  += (_, _) => UpdateMode();
        ImageMode.IsCheckedChanged += (_, _) => UpdateMode();
        TextMode.IsCheckedChanged  += (_, _) => UpdateMode();

        ClearButton.Click  += (_, _) => Pad.Clear();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        SaveButton.Click   += (_, _) => OnSave();
        CancelButton.Click += (_, _) => Close(false);

        UpdateMode();
    }

    private void UpdateMode()
    {
        DrawPanel.IsVisible  = DrawMode.IsChecked == true;
        ImagePanel.IsVisible = ImageMode.IsChecked == true;
        TextPanel.IsVisible  = TextMode.IsChecked == true;
    }

    private async Task BrowseAsync()
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Instance["Picker.ChooseImage"],
            AllowMultiple = false,
            // PNG only: a signature must be transparent, and JPEG/BMP/GIF would overlay an opaque box.
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Instance["Picker.PngImages"])
                {
                    Patterns = ["*.png"],
                },
            ],
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        _imageBytes = await File.ReadAllBytesAsync(path);
        ImagePathBox.Text = Path.GetFileName(path);
        using var ms = new MemoryStream(_imageBytes);
        ImagePreview.Source = new Bitmap(ms);
    }

    private void OnSave()
    {
        byte[]? png;

        if (DrawMode.IsChecked == true)
        {
            if (!Pad.HasInk) { ShowError(); return; }
            png = RenderVisualToPng(Pad, (int)Math.Ceiling(Pad.Bounds.Width), (int)Math.Ceiling(Pad.Bounds.Height));
        }
        else if (ImageMode.IsChecked == true)
        {
            if (_imageBytes is null) { ShowError(); return; }
            png = _imageBytes;
        }
        else
        {
            var text = TextInput.Text;
            if (string.IsNullOrWhiteSpace(text)) { ShowError(); return; }
            png = RenderTextToPng(text);
        }

        if (png is null || png.Length == 0) { ShowError(); return; }

        ResultPng = png;
        ResultName = string.IsNullOrWhiteSpace(NameInput.Text)
            ? Localizer.Instance["SigCreate.DefaultName"]
            : NameInput.Text!.Trim();
        Close(true);
    }

    private void ShowError()
    {
        ErrorLabel.Text = Localizer.Instance["SigCreate.Empty"];
        ErrorBanner.IsVisible = true;
    }

    private static byte[] RenderVisualToPng(Visual visual, int width, int height)
    {
        var rtb = new RenderTargetBitmap(
            new PixelSize(Math.Max(1, width), Math.Max(1, height)), new Vector(96, 96));
        rtb.Render(visual);
        using var ms = new MemoryStream();
        rtb.Save(ms);
        return ms.ToArray();
    }

    private static byte[] RenderTextToPng(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe Script, Brush Script MT"),
            FontStyle = FontStyle.Italic,
            FontSize = 48,
            Foreground = Brushes.Black,
        };
        // A transparent-padded host so cursive glyphs that overshoot the text box aren't clipped.
        var host = new Border { Child = label, Padding = new Thickness(12), Background = Brushes.Transparent };
        host.Measure(Size.Infinity);
        host.Arrange(new Rect(host.DesiredSize));

        return RenderVisualToPng(host,
            (int)Math.Ceiling(host.DesiredSize.Width),
            (int)Math.Ceiling(host.DesiredSize.Height));
    }
}
