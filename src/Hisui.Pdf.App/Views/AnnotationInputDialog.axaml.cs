using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

public partial class AnnotationInputDialog : Window
{
    private readonly AnnotationTool _tool;
    private byte[]? _imageBytes;

    public AnnotationInputModel? Result { get; private set; }

    /// <summary>The current text in the editor — used by the TextEdit replacement flow.</summary>
    public string InputText => GetText("AnnotTextBox") ?? string.Empty;

    public AnnotationInputDialog(AnnotationTool tool, string? initialText = null)
    {
        InitializeComponent();
        _tool = tool;

        ConfigureForTool(tool, initialText);
        WireButtons();
        WireBrowseImage();

        // Focus the most relevant field once the dialog is shown.
        Opened += (_, _) =>
        {
            if (RequiresText(tool)) this.FindControl<TextBox>("AnnotTextBox")?.Focus();
            else this.FindControl<TextBox>("ColorInput")?.Focus();
        };
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    private void ConfigureForTool(AnnotationTool tool, string? initialText)
    {
        SetText("ToolLabel", ToolDisplayName(tool));
        SetText("HintLabel", ToolHint(tool));

        switch (tool)
        {
            case AnnotationTool.Highlight:
                Show("ColorSection");
                Show("OpacitySection");
                SetText("ColorInput", "#FFFF00");
                break;

            case AnnotationTool.Rectangle:
                Show("ColorSection");
                Show("FillSection");
                Show("LineWidthSection");
                SetText("ColorInput", "#FF0000");
                break;

            case AnnotationTool.FreeText:
                Show("TextSection");
                Show("ColorSection");
                SetText("ColorInput", "#000000");
                SetText("TextLabel", Localizer.Instance["Label.Text"]);
                if (initialText is not null) SetText("AnnotTextBox", initialText);
                break;

            case AnnotationTool.StickyNote:
                Show("TextSection");
                SetText("TextLabel", Localizer.Instance["Label.Note"]);
                if (initialText is not null) SetText("AnnotTextBox", initialText);
                break;

            case AnnotationTool.TextEdit:
                Show("TextSection");
                SetText("TextLabel", Localizer.Instance["Label.NewText"]);
                if (initialText is not null) SetText("AnnotTextBox", initialText);
                break;

            case AnnotationTool.ImageOverlay:
                Show("ImageSection");
                break;

            case AnnotationTool.Watermark:
                Show("TextSection");
                Show("ColorSection");
                Show("OpacitySection");
                Show("WatermarkSection");
                SetText("ColorInput", "#808080");
                SetText("TextLabel", Localizer.Instance["Label.WatermarkText"]);
                SetNumber("OpacityInput", 0.25);
                break;
        }
    }

    private void WireButtons()
    {
        if (this.FindControl<Button>("ApplyButton") is { } applyBtn)
            applyBtn.Click += (_, _) => ApplyAndClose();
        if (this.FindControl<Button>("CancelButton") is { } cancelBtn)
            cancelBtn.Click += (_, _) => Close(false);
    }

    private void WireBrowseImage()
    {
        if (this.FindControl<Button>("BrowseImageButton") is { } browseBtn)
            browseBtn.Click += async (_, _) => await PickImageAsync();
    }

    // ── Apply + validation ────────────────────────────────────────────────────

    private void ApplyAndClose()
    {
        var text = GetText("AnnotTextBox") ?? string.Empty;

        if (RequiresText(_tool) && string.IsNullOrWhiteSpace(text))
        {
            ShowError(Localizer.Instance["Error.TextRequired"]);
            return;
        }
        if (_tool == AnnotationTool.ImageOverlay && _imageBytes is null)
        {
            ShowError(Localizer.Instance["Error.ImageRequired"]);
            return;
        }

        var colorHex = NormalizeHex(GetText("ColorInput"), "#000000");
        var opacity = Math.Clamp(GetNumber("OpacityInput", 0.4), 0.1, 1.0);
        var lineWidth = Math.Max(GetNumber("LineWidthInput", 2.0), 0.1);
        var wmFontSize = Math.Max(GetNumber("WmFontSizeInput", 48), 6);
        var wmRotation = GetNumber("WmRotationInput", 45);

        string? fillHex = null;
        if (this.FindControl<CheckBox>("FillCheck")?.IsChecked == true)
            fillHex = NormalizeHex(GetText("FillInput"), "#FFFFFF");

        WatermarkOptions? watermarkOpts = null;
        if (_tool == AnnotationTool.Watermark)
            watermarkOpts = new WatermarkOptions
            {
                ColorHex = colorHex,
                Opacity = opacity,
                FontSizePoints = wmFontSize,
                RotationDegrees = wmRotation,
            };

        Result = new AnnotationInputModel
        {
            Tool = _tool,
            Text = text,
            ColorHex = colorHex,
            FillHex = fillHex,
            Opacity = opacity,
            LineWidth = lineWidth,
            ImageBytes = _imageBytes,
            WatermarkOptions = watermarkOpts,
        };

        Close(true);
    }

    private static bool RequiresText(AnnotationTool tool) => tool is
        AnnotationTool.FreeText or AnnotationTool.StickyNote or
        AnnotationTool.TextEdit or AnnotationTool.Watermark;

    private void ShowError(string message)
    {
        if (this.FindControl<TextBlock>("ErrorLabel") is { } label) label.Text = message;
        if (this.FindControl<Border>("ErrorBanner") is { } banner) banner.IsVisible = true;
    }

    // ── Image picker ──────────────────────────────────────────────────────────

    private async Task PickImageAsync()
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return;

        var imageType = new FilePickerFileType(Localizer.Instance["Picker.Images"])
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif"],
        };

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Instance["Picker.ChooseImage"],
            AllowMultiple = false,
            FileTypeFilter = [imageType],
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;

        _imageBytes = await File.ReadAllBytesAsync(path);
        SetText("ImagePathBox", Path.GetFileName(path));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Show(string controlName)
    {
        if (this.FindControl<Control>(controlName) is { } ctrl)
            ctrl.IsVisible = true;
    }

    // Looks up by base Control type to avoid Avalonia's FindControl<T> throwing on a type mismatch.
    private void SetText(string controlName, string value)
    {
        switch (this.FindControl<Control>(controlName))
        {
            case TextBox tb: tb.Text = value; break;
            case TextBlock tbl: tbl.Text = value; break;
        }
    }

    private string? GetText(string controlName) =>
        this.FindControl<Control>(controlName) is TextBox tb ? tb.Text : null;

    private double GetNumber(string controlName, double fallback) =>
        this.FindControl<Control>(controlName) is NumericUpDown { Value: { } d } ? (double)d : fallback;

    private void SetNumber(string controlName, double value)
    {
        if (this.FindControl<Control>(controlName) is NumericUpDown nud)
            nud.Value = (decimal)value;
    }

    private static string NormalizeHex(string? value, string fallback)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return fallback;
        if (!s.StartsWith('#')) s = "#" + s;
        return (s.Length == 4 || s.Length == 7) ? s : fallback;
    }

    private static string ToolDisplayName(AnnotationTool tool) => Localizer.Instance[tool switch
    {
        AnnotationTool.Highlight    => "DialogTool.Highlight",
        AnnotationTool.Rectangle    => "DialogTool.Rectangle",
        AnnotationTool.FreeText     => "DialogTool.FreeText",
        AnnotationTool.StickyNote   => "DialogTool.StickyNote",
        AnnotationTool.TextEdit     => "DialogTool.TextEdit",
        AnnotationTool.ImageOverlay => "DialogTool.ImageOverlay",
        AnnotationTool.Watermark    => "DialogTool.Watermark",
        _                           => "DialogTool.Default",
    }];

    private static string ToolHint(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Highlight    => Localizer.Instance["Hint.Highlight"],
        AnnotationTool.Rectangle    => Localizer.Instance["Hint.Rectangle"],
        AnnotationTool.FreeText     => Localizer.Instance["Hint.FreeText"],
        AnnotationTool.StickyNote   => Localizer.Instance["Hint.StickyNote"],
        AnnotationTool.TextEdit     => Localizer.Instance["Hint.TextEdit"],
        AnnotationTool.ImageOverlay => Localizer.Instance["Hint.ImageOverlay"],
        AnnotationTool.Watermark    => Localizer.Instance["Hint.Watermark"],
        _                           => string.Empty,
    };
}
