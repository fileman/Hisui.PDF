using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hisui.Pdf.App.Imaging;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.Services;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using Hisui.Pdf.Core.Printing;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// In-app print dialog: printer, copies, duplex, page range, colour mode, layout (size / poster / n-up /
/// booklet), orientation, paper source and size, plus the printer driver's own properties. Every change
/// re-runs <see cref="PrintLayoutEngine"/> and previews the resulting sheets; the same sheets go into
/// <see cref="Result"/>, so the printout matches the preview.
/// </summary>
public partial class PrintDialog : Window
{
    // Preview raster resolution for a page shown at 100%; scaled with the page's size on the sheet.
    private const double PreviewDpi = 110;

    private static readonly IBrush MarkBrush = new SolidColorBrush(Color.Parse("#E24B4A"));
    private static readonly IBrush BorderBrushGrey = new SolidColorBrush(Color.Parse("#6E6E6E"));

    private readonly IPrintService _print;
    private readonly IPdfRenderer _renderer;
    private readonly byte[] _pdf;
    private readonly int _pageCount;
    private readonly int _currentIndex;
    private readonly Dictionary<(int Page, PdfRenderColorMode Mode, int Dpi), Bitmap> _previewCache = [];

    private IReadOnlyList<PdfPageSize> _pageSizes = [];
    private PrinterCapabilities _capabilities = PrinterCapabilities.Fallback;
    private byte[]? _driverSettings;
    private IReadOnlyList<PrintSheet> _sheets = [];
    private int _sheetIndex;
    private int _renderToken;
    private bool _ready; // false while controls are being populated, so changes don't trigger rebuilds
    private bool _scrolledToCurrent;
    private bool _rebuildQueued;

    public PrintJob? Result { get; private set; }

    // Parameterless ctor for the XAML designer / Avalonia tooling only.
    public PrintDialog() : this(null!, null!, [], 0, 0) { }

    public PrintDialog(IPrintService print, IPdfRenderer renderer, byte[] pdf, int pageCount, int currentIndex)
    {
        InitializeComponent();

        _print = print;
        _renderer = renderer;
        _pdf = pdf;
        _pageCount = pageCount;
        _currentIndex = Math.Clamp(currentIndex, 0, Math.Max(0, pageCount - 1));

        Title = Localizer.Instance["Print.Title"];
        PropertiesButton.IsVisible = print?.SupportsSystemDialog == true;

        if (print is not null) PopulatePrinters();

        PrinterCombo.SelectionChanged += (_, _) => OnPrinterChanged();
        PropertiesButton.Click += (_, _) => OnPrinterProperties();

        RangeCombo.SelectionChanged += (_, _) =>
        {
            RangeBox.IsVisible = RangeCombo.SelectedIndex == 2;
            Rebuild();
        };
        RangeBox.TextChanged += (_, _) => Rebuild();

        foreach (var radio in new[] { SizeModeRadio, PosterModeRadio, MultipleModeRadio, BookletModeRadio })
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) OnModeChanged(radio); };

        CustomRadio.IsCheckedChanged += (_, _) => CustomScaleBox.IsEnabled = CustomRadio.IsChecked == true;

        // Everything else just re-lays out the sheets. (A radio reports "checked" before its group
        // unchecks the previous one, which is why Rebuild defers until the group has settled.)
        foreach (var radio in new[] { ColorRadio, GreyRadio, BlackTextRadio, FitRadio, ShrinkRadio, ActualRadio, CustomRadio })
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) Rebuild(); };
        foreach (var check in new[] { MatchPaperCheck, CutMarksCheck, LabelsCheck, BordersCheck, AutoRotateCheck })
            check.IsCheckedChanged += (_, _) => Rebuild();
        foreach (var box in new[] { CustomScaleBox, PosterScaleBox, PosterOverlapBox, GutterBox, SignatureBox })
            box.ValueChanged += (_, _) => Rebuild();
        foreach (var combo in new[] { PerSheetCombo, OrderCombo, BindingCombo, SidesCombo, OrientationCombo, PaperCombo })
            combo.SelectionChanged += (_, _) => Rebuild();

        PrevButton.Click += (_, _) => StepPreview(-1);
        NextButton.Click += (_, _) => StepPreview(+1);
        CancelButton.Click += (_, _) => Close(false);
        PrintButton.Click += (_, _) => OnPrint();
        this.WireAcceptCancel(OnPrint, () => Close(false));

        Opened += async (_, _) =>
        {
            PrinterCombo.Focus();
            try
            {
                if (_renderer is not null && _pdf.Length > 0)
                    _pageSizes = await _renderer.GetPageSizesAsync(_pdf);
            }
            catch
            {
                // Fall back to A4 for every page; the preview still shows the arrangement.
            }
            _ready = true;
            Rebuild();
        };

        // Preview bitmaps are native-backed IDisposable — release them when the dialog closes
        // (same convention as SignatureManagerDialog).
        Closed += (_, _) =>
        {
            SheetCanvas.Children.Clear();
            foreach (var bitmap in _previewCache.Values) bitmap.Dispose();
            _previewCache.Clear();
        };
    }

    // ── Printer ───────────────────────────────────────────────────────────────

    private void PopulatePrinters()
    {
        var printers = _print.GetPrinters();
        PrinterCombo.ItemsSource = printers;

        if (printers.Count == 0)
        {
            NoPrintersLabel.IsVisible = true;
            PrinterCombo.IsEnabled = false;
            PrintButton.IsEnabled = false;
            PropertiesButton.IsEnabled = false;
            ApplyCapabilities(PrinterCapabilities.Fallback);
            return;
        }

        var preferred = _print.DefaultPrinter;
        var index = preferred is not null ? IndexOf(printers, preferred) : 0;
        PrinterCombo.SelectedIndex = index < 0 ? 0 : index;
        OnPrinterChanged();
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (var i = 0; i < items.Count; i++)
            if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private void OnPrinterChanged()
    {
        if (_print is null) return;
        _driverSettings = null; // driver settings belong to the printer they were made for
        var printer = PrinterCombo.SelectedItem as string;
        ApplyCapabilities(printer is null ? PrinterCapabilities.Fallback : _print.GetCapabilities(printer));
        Rebuild();
    }

    private void ApplyCapabilities(PrinterCapabilities capabilities)
    {
        var wasReady = _ready;
        _ready = false;
        _capabilities = capabilities;

        DuplexCombo.IsEnabled = capabilities.CanDuplex;
        if (!capabilities.CanDuplex) DuplexCombo.SelectedIndex = 0; // fall back to single-sided

        var sources = new List<string> { Localizer.Instance["Print.Source.Auto"] };
        sources.AddRange(capabilities.PaperSources);
        SourceCombo.ItemsSource = sources;
        SourceCombo.SelectedIndex = 0;

        PaperCombo.ItemsSource = capabilities.PaperSizes.Select(p => p.Name).ToList();
        PaperCombo.SelectedIndex = capabilities.DefaultPaperIndex;

        var parts = new List<string>
        {
            Localizer.Instance[capabilities.SupportsColor ? "Print.Info.Color" : "Print.Info.Mono"],
        };
        if (capabilities.CanDuplex) parts.Add(Localizer.Instance["Print.Info.Duplex"]);
        if (capabilities.PaperSources.Count > 0)
            parts.Add(Localizer.Instance.Format("Print.Info.Sources", capabilities.PaperSources.Count));
        PrinterInfo.Text = string.Join(" · ", parts);

        _ready = wasReady;
    }

    private void OnPrinterProperties()
    {
        if (PrinterCombo.SelectedItem is not string printer) return;
        var owner = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        PrinterDriverSettings? settings;
        try
        {
            settings = _print.ShowPrinterProperties(printer, owner, _driverSettings);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }
        if (settings is null) return;

        // Mirror what the driver dialog changed into our own controls, then keep the driver blob for
        // everything else it holds (quality, finishing, …).
        _ready = false;
        _driverSettings = settings.DevMode;
        CopiesBox.Value = Math.Clamp(settings.Copies, 1, 99);
        if (DuplexCombo.IsEnabled)
            DuplexCombo.SelectedIndex = settings.Duplex switch
            {
                PrintDuplex.LongEdge => 1,
                PrintDuplex.ShortEdge => 2,
                _ => 0,
            };
        if (!settings.Color) GreyRadio.IsChecked = true;
        if (settings.Landscape) OrientationCombo.SelectedIndex = 2;
        SelectByName(SourceCombo, settings.PaperSource);
        SelectByName(PaperCombo, settings.PaperSize);
        _ready = true;
        Rebuild();
    }

    private static void SelectByName(ComboBox combo, string? name)
    {
        if (string.IsNullOrEmpty(name) || combo.ItemsSource is not IReadOnlyList<string> items) return;
        var index = IndexOf(items, name);
        if (index >= 0) combo.SelectedIndex = index;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void OnModeChanged(RadioButton selected)
    {
        // Keyed on the radio that was just checked: its siblings may not have been unchecked yet.
        SizePanel.IsVisible = selected == SizeModeRadio;
        PosterPanel.IsVisible = selected == PosterModeRadio;
        MultiplePanel.IsVisible = selected == MultipleModeRadio;
        BookletPanel.IsVisible = selected == BookletModeRadio;

        // A two-sided booklet is folded across the short edge, so that is how the duplexer must flip.
        if (selected == BookletModeRadio && DuplexCombo.IsEnabled && SidesCombo.SelectedIndex == 0)
            DuplexCombo.SelectedIndex = 2;

        Rebuild();
    }

    private PdfRenderColorMode ColorMode =>
        GreyRadio.IsChecked == true ? PdfRenderColorMode.Grayscale
        : BlackTextRadio.IsChecked == true ? PdfRenderColorMode.BlackText
        : PdfRenderColorMode.Color;

    private PrintLayoutOptions ReadOptions() => new()
    {
        Mode = PosterModeRadio.IsChecked == true ? PrintLayoutMode.Poster
            : MultipleModeRadio.IsChecked == true ? PrintLayoutMode.Multiple
            : BookletModeRadio.IsChecked == true ? PrintLayoutMode.Booklet
            : PrintLayoutMode.Size,
        Orientation = OrientationCombo.SelectedIndex switch
        {
            1 => PrintOrientation.Portrait,
            2 => PrintOrientation.Landscape,
            _ => PrintOrientation.Auto,
        },
        Scaling = FitRadio.IsChecked == true ? PrintScaling.Fit
            : ActualRadio.IsChecked == true ? PrintScaling.ActualSize
            : CustomRadio.IsChecked == true ? PrintScaling.Custom
            : PrintScaling.ShrinkOversized,
        CustomScale = (double)(CustomScaleBox.Value ?? 100),
        MatchPaperToPage = MatchPaperCheck.IsChecked == true,
        PosterScale = (double)(PosterScaleBox.Value ?? 200),
        PosterOverlap = (double)(PosterOverlapBox.Value ?? 10) * PrintLayoutEngine.PointsPerMillimetre,
        PosterCutMarks = CutMarksCheck.IsChecked == true,
        PosterLabels = LabelsCheck.IsChecked == true,
        PagesPerSheet = PrintLayoutEngine.SupportedPagesPerSheet[Math.Clamp(PerSheetCombo.SelectedIndex, 0, PrintLayoutEngine.SupportedPagesPerSheet.Count - 1)],
        PageOrder = OrderCombo.SelectedIndex == 1 ? PrintPageOrder.Vertical : PrintPageOrder.Horizontal,
        PageBorders = BordersCheck.IsChecked == true,
        AutoRotatePages = AutoRotateCheck.IsChecked == true,
        Binding = BindingCombo.SelectedIndex == 1 ? BookletBinding.Right : BookletBinding.Left,
        BookletSides = SidesCombo.SelectedIndex switch
        {
            1 => BookletSides.FrontOnly,
            2 => BookletSides.BackOnly,
            _ => BookletSides.Both,
        },
        BookletGutter = (double)(GutterBox.Value ?? 0) * PrintLayoutEngine.PointsPerMillimetre,
        SignatureSheets = (int)(SignatureBox.Value ?? 0),
    };

    private PrintSize PageSize(int index) =>
        index < _pageSizes.Count
            ? new PrintSize(_pageSizes[index].Width, _pageSizes[index].Height)
            : new PrintSize(595.276, 841.89);

    /// <summary>
    /// Schedules a re-layout from the current control state. Deferred to the next dispatcher pass so a
    /// burst of changes (a radio group switching, a printer change refilling combos) lays out once, and
    /// only after every control has settled.
    /// </summary>
    private void Rebuild()
    {
        if (!_ready || _rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            RebuildNow();
        });
    }

    private void RebuildNow()
    {
        if (!_ready) return;
        ErrorLabel.IsVisible = false;

        var pages = ResolvePages();
        if (pages is null || pages.Count == 0)
        {
            _sheets = [];
            if (RangeCombo.SelectedIndex == 2 && !string.IsNullOrWhiteSpace(RangeBox.Text))
                ShowError(Localizer.Instance["Print.InvalidRange"]);
            _ = ShowPreviewAsync();
            return;
        }

        var papers = _capabilities.PaperSizes.Select(p => p.ToLayoutPaper()).ToList();
        _sheets = PrintLayoutEngine.Build(pages, PageSize, papers, Math.Max(0, PaperCombo.SelectedIndex), ReadOptions());

        if (!_scrolledToCurrent)
        {
            // First layout: open on the sheet showing the page the user was looking at.
            _scrolledToCurrent = true;
            var found = -1;
            for (var i = 0; i < _sheets.Count && found < 0; i++)
                if (_sheets[i].Placements.Any(p => p.PageIndex == _currentIndex)) found = i;
            _sheetIndex = Math.Max(0, found);
        }
        _sheetIndex = Math.Clamp(_sheetIndex, 0, Math.Max(0, _sheets.Count - 1));
        _ = ShowPreviewAsync();
    }

    /// <summary>
    /// Zero-based page indices for the range choice: all, current, a typed range, or odd / even page numbers.
    /// Null when the typed range is invalid.
    /// </summary>
    private IReadOnlyList<int>? ResolvePages() => RangeCombo.SelectedIndex switch
    {
        1 => [_currentIndex],
        2 => ParseRange(RangeBox.Text, _pageCount),
        3 => Enumerable.Range(0, _pageCount).Where(i => i % 2 == 0).ToArray(), // pages 1, 3, 5, …
        4 => Enumerable.Range(0, _pageCount).Where(i => i % 2 == 1).ToArray(), // pages 2, 4, 6, …
        _ => Enumerable.Range(0, _pageCount).ToArray(),
    };

    // ── Preview ───────────────────────────────────────────────────────────────

    private void StepPreview(int delta)
    {
        var next = Math.Clamp(_sheetIndex + delta, 0, Math.Max(0, _sheets.Count - 1));
        if (next == _sheetIndex) return;
        _sheetIndex = next;
        _ = ShowPreviewAsync();
    }

    private async Task ShowPreviewAsync()
    {
        var token = ++_renderToken;
        SheetCanvas.Children.Clear();
        PrevButton.IsEnabled = _sheetIndex > 0;
        NextButton.IsEnabled = _sheetIndex < _sheets.Count - 1;
        PrintButton.IsEnabled = _sheets.Count > 0 && PrinterCombo.SelectedItem is not null;

        if (_sheets.Count == 0)
        {
            PageLabel.Text = Localizer.Instance["Print.Preview.Empty"];
            InfoLabel.Text = string.Empty;
            return;
        }

        var sheet = _sheets[_sheetIndex];
        var options = ReadOptions();
        var paper = sheet.Landscape
            ? new PrintSize(sheet.Paper.Height, sheet.Paper.Width)
            : new PrintSize(sheet.Paper.Width, sheet.Paper.Height);
        PaperHost.Width = paper.Width;
        PaperHost.Height = paper.Height;
        PaperHost.Padding = new Thickness(sheet.Paper.Margin);
        SheetCanvas.Width = sheet.Size.Width;
        SheetCanvas.Height = sheet.Size.Height;

        PageLabel.Text = Localizer.Instance.Format("Print.Preview.Page", _sheetIndex + 1, _sheets.Count);
        InfoLabel.Text = options.Mode == PrintLayoutMode.Size && sheet.Placements.Count == 1
            ? Localizer.Instance.Format("Print.Preview.Scale", sheet.Paper.Name,
                Math.Round(100 * sheet.Placements[0].Bounds.Width / PageSize(sheet.Placements[0].PageIndex).Width))
            : Localizer.Instance.Format("Print.Preview.Sheets", sheet.Paper.Name, _sheets.Count);

        // Lay out placeholders synchronously so the arrangement shows at once; rasters fill in after.
        var images = new List<(Image Image, PrintPlacement Placement)>();
        foreach (var placement in sheet.Placements)
        {
            var b = placement.Bounds;
            var image = new Image { Stretch = Stretch.Fill };
            var frame = new Border { Background = Brushes.White, Child = image };
            if (placement.Rotated)
            {
                // Lay the page out upright in a box of swapped size centred on the target, then turn it.
                frame.Width = b.Height;
                frame.Height = b.Width;
                Canvas.SetLeft(frame, b.X + (b.Width - b.Height) / 2);
                Canvas.SetTop(frame, b.Y + (b.Height - b.Width) / 2);
                frame.RenderTransformOrigin = RelativePoint.Center;
                frame.RenderTransform = new RotateTransform(90);
            }
            else
            {
                frame.Width = b.Width;
                frame.Height = b.Height;
                Canvas.SetLeft(frame, b.X);
                Canvas.SetTop(frame, b.Y);
            }
            SheetCanvas.Children.Add(frame);
            images.Add((image, placement));

            if (placement.Border)
            {
                var border = new Rectangle { Width = b.Width, Height = b.Height, Stroke = BorderBrushGrey, StrokeThickness = 0.75 };
                Canvas.SetLeft(border, b.X);
                Canvas.SetTop(border, b.Y);
                SheetCanvas.Children.Add(border);
            }
        }

        foreach (var mark in sheet.Marks)
            SheetCanvas.Children.Add(new Line
            {
                StartPoint = new Point(mark.X1, mark.Y1),
                EndPoint = new Point(mark.X2, mark.Y2),
                Stroke = MarkBrush,
                StrokeThickness = 1,
                StrokeDashArray = [6, 4],
            });

        foreach (var label in sheet.Labels)
        {
            var text = new TextBlock { Text = label.Text, FontSize = 7, Foreground = Brushes.DimGray, Background = Brushes.White, Padding = new Thickness(1, 0) };
            Canvas.SetLeft(text, label.X - 1);
            Canvas.SetTop(text, label.Y);
            SheetCanvas.Children.Add(text);
        }

        var mode = ColorMode;
        foreach (var (image, placement) in images)
        {
            var bitmap = await GetPreviewAsync(placement, mode);
            if (token != _renderToken) return; // a newer layout or navigation replaced this one
            image.Source = bitmap;
        }
    }

    private async Task<Bitmap?> GetPreviewAsync(PrintPlacement placement, PdfRenderColorMode mode)
    {
        var page = PageSize(placement.PageIndex);
        var shown = placement.Rotated ? placement.Bounds.Height : placement.Bounds.Width;
        // Bucket the resolution so small layout tweaks reuse the cached raster.
        var dpi = (int)Math.Clamp(Math.Round(PreviewDpi * shown / page.Width / 10) * 10, 30, 200);
        var key = (placement.PageIndex, mode, dpi);
        if (_previewCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var rendered = await _renderer.RenderPageAsync(_pdf, placement.PageIndex, dpi, new PdfRenderOptions(mode, ForPrinting: true));
            var bitmap = BitmapConversions.ToFrozenBitmap(rendered);
            _previewCache[key] = bitmap;
            return bitmap;
        }
        catch
        {
            return null; // a transient render failure shouldn't take the dialog down
        }
    }

    // ── Print ─────────────────────────────────────────────────────────────────

    private void OnPrint()
    {
        var printer = PrinterCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(printer)) { ShowError(Localizer.Instance["Print.NoPrinters"]); return; }

        if (_rebuildQueued) RebuildNow(); // e.g. Enter pressed straight after typing a range
        if (_sheets.Count == 0) { ShowError(Localizer.Instance["Print.InvalidRange"]); return; }

        Result = new PrintJob
        {
            PrinterName = printer,
            Copies = (int)(CopiesBox.Value ?? 1),
            Duplex = DuplexCombo.SelectedIndex switch
            {
                1 => PrintDuplex.LongEdge,
                2 => PrintDuplex.ShortEdge,
                _ => PrintDuplex.Simplex,
            },
            ColorMode = ColorMode,
            PaperSource = SourceCombo.SelectedIndex > 0 ? SourceCombo.SelectedItem as string : null,
            Sheets = _sheets,
            DriverSettings = _driverSettings,
        };
        Close(true);
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }

    /// <summary>
    /// Parses a page-range string such as "1-5, 8, 11-13" into ordered, de-duplicated zero-based indices.
    /// 1-based input; tokens separated by comma / semicolon / space; reversed ranges are tolerated.
    /// Returns null if any token is malformed or refers outside 1..<paramref name="pageCount"/>.
    /// </summary>
    internal static IReadOnlyList<int>? ParseRange(string? text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text) || pageCount <= 0) return null;

        var pages = new List<int>();
        var seen = new HashSet<int>();

        foreach (var raw in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(token, out var n) || !Add(n)) return null;
            }
            else
            {
                var left = token[..dash].Trim();
                var right = token[(dash + 1)..].Trim();
                if (!int.TryParse(left, out var a) || !int.TryParse(right, out var b)) return null;
                if (a > b) (a, b) = (b, a);
                for (var n = a; n <= b; n++)
                    if (!Add(n)) return null;
            }
        }

        return pages.Count > 0 ? pages : null;

        bool Add(int oneBased)
        {
            if (oneBased < 1 || oneBased > pageCount) return false;
            var index = oneBased - 1;
            if (seen.Add(index)) pages.Add(index);
            return true;
        }
    }
}
