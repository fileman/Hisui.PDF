using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Hisui.Pdf.App.Imaging;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.Services;
using Hisui.Pdf.Core.Abstractions;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// In-app print dialog: pick a printer, copies, duplex mode and a page range, with a live page preview.
/// The resolved request is exposed via <see cref="Result"/> when the dialog closes with true. Page
/// rendering reuses the app's PDF renderer so the preview matches what the printer receives.
/// </summary>
public partial class PrintDialog : Window
{
    private const int PreviewDpi = 120;

    private readonly IPrintService _print;
    private readonly IPdfRenderer _renderer;
    private readonly byte[] _pdf;
    private readonly int _pageCount;
    private readonly int _currentIndex;
    private readonly Dictionary<int, Bitmap> _previewCache = [];

    private int _previewIndex;
    private int _renderToken;

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
        _previewIndex = _currentIndex;

        Title = Localizer.Instance["Print.Title"];

        if (print is not null) PopulatePrinters();

        DuplexCombo.SelectionChanged += (_, _) => { };
        PrinterCombo.SelectionChanged += (_, _) => UpdateDuplexAvailability();
        AllRadio.IsCheckedChanged += (_, _) => OnRangeModeChanged();
        CurrentRadio.IsCheckedChanged += (_, _) => OnRangeModeChanged();
        RangeRadio.IsCheckedChanged += (_, _) => OnRangeModeChanged();

        PrevButton.Click += (_, _) => StepPreview(-1);
        NextButton.Click += (_, _) => StepPreview(+1);
        CancelButton.Click += (_, _) => Close(false);
        PrintButton.Click += (_, _) => OnPrint();

        Opened += (_, _) => _ = ShowPreviewAsync();

        // Preview bitmaps are native-backed IDisposable — release them when the dialog closes
        // (same convention as SignatureManagerDialog).
        Closed += (_, _) =>
        {
            PreviewImage.Source = null;
            foreach (var bitmap in _previewCache.Values) bitmap.Dispose();
            _previewCache.Clear();
        };
    }

    private void PopulatePrinters()
    {
        var printers = _print.GetPrinters();
        PrinterCombo.ItemsSource = printers;

        if (printers.Count == 0)
        {
            NoPrintersLabel.IsVisible = true;
            PrinterCombo.IsEnabled = false;
            PrintButton.IsEnabled = false;
            return;
        }

        var preferred = _print.DefaultPrinter;
        var index = preferred is not null ? IndexOf(printers, preferred) : 0;
        PrinterCombo.SelectedIndex = index < 0 ? 0 : index;
        UpdateDuplexAvailability();
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (var i = 0; i < items.Count; i++)
            if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private void UpdateDuplexAvailability()
    {
        var printer = PrinterCombo.SelectedItem as string;
        var canDuplex = printer is not null && _print.SupportsDuplex(printer);
        DuplexCombo.IsEnabled = canDuplex;
        if (!canDuplex) DuplexCombo.SelectedIndex = 0; // fall back to single-sided
    }

    private void OnRangeModeChanged() => RangeBox.IsEnabled = RangeRadio.IsChecked == true;

    private void StepPreview(int delta)
    {
        var next = Math.Clamp(_previewIndex + delta, 0, Math.Max(0, _pageCount - 1));
        if (next == _previewIndex) return;
        _previewIndex = next;
        _ = ShowPreviewAsync();
    }

    private async Task ShowPreviewAsync()
    {
        if (_pageCount <= 0) return;
        PageLabel.Text = Localizer.Instance.Format("Print.Preview.Page", _previewIndex + 1, _pageCount);

        var index = _previewIndex;
        var token = ++_renderToken;

        if (!_previewCache.TryGetValue(index, out var bitmap))
        {
            try
            {
                var png = await _renderer.RenderPageToPngAsync(_pdf, index, PreviewDpi);
                bitmap = BitmapConversions.ToFrozenBitmap(png);
                _previewCache[index] = bitmap;
            }
            catch
            {
                return; // a transient render failure shouldn't take the dialog down
            }
        }

        if (token == _renderToken) // ignore results that arrive after a newer navigation
            PreviewImage.Source = bitmap;
    }

    private void OnPrint()
    {
        var printer = PrinterCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(printer)) { ShowError(Localizer.Instance["Print.NoPrinters"]); return; }

        var pages = ResolvePages();
        if (pages is null || pages.Count == 0) { ShowError(Localizer.Instance["Print.InvalidRange"]); return; }

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
            Pages = pages,
        };
        Close(true);
    }

    private IReadOnlyList<int>? ResolvePages()
    {
        if (CurrentRadio.IsChecked == true) return [_currentIndex];
        if (RangeRadio.IsChecked == true) return ParseRange(RangeBox.Text, _pageCount);
        return Enumerable.Range(0, _pageCount).ToArray(); // All
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
