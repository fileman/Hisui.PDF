using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// An annotation placed on the page but not burned in yet: a box on the annotation canvas that is dragged to move,
/// dragged by its corner to resize and, for free text, typed into, with a small toolbar for the text settings
/// (size, character spacing for boxed forms such as IBAN fields, colour, background).
/// Enter or ✓ commits it through <see cref="MainViewModel.CommitPendingAsync"/>; Esc or ✕ cancels.
/// </summary>
public sealed class PendingAnnotationBox : Border
{
    private const double MinSize = 8, ToolbarGap = 6, ToolbarHeight = 40;
    private static readonly FontFamily TextFamily = new("Arial, Liberation Sans, Helvetica, sans-serif");
    private static readonly FontFamily CellFamily = new("Consolas, Liberation Mono, Courier New, monospace");

    private readonly MainViewModel _vm;
    private readonly Canvas _canvas;
    private readonly AnnotationInputModel _model;
    private readonly double _scale; // preview pixels per PDF point
    private readonly double _canvasW, _canvasH;
    private readonly Border _toolbar;
    private TextBox? _editor;
    private NumericUpDown? _size, _pitch;
    private TextBox? _color;
    private CheckBox? _background;
    private bool _done;

    public PendingAnnotationBox(MainViewModel vm, AnnotationInputModel model, Canvas canvas)
    {
        _vm = vm;
        _model = model;
        _canvas = canvas;
        _scale = vm.PixelsPerPoint;
        (_canvasW, _canvasH) = (canvas.Bounds.Width, canvas.Bounds.Height);

        var r = model.Rect;
        Width = Math.Max(MinSize, r.Width * _canvasW);
        Height = Math.Max(MinSize, r.Height * _canvasH);
        Canvas.SetLeft(this, r.Left * _canvasW);
        Canvas.SetTop(this, r.Top * _canvasH);
        Background = Brushes.Transparent;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);

        var accent = Resource("RibbonAccentBrush") ?? Brushes.DodgerBlue;
        var panel = new Panel();
        panel.Children.Add(BuildContent(model));
        panel.Children.Add(new Border { BorderBrush = accent, BorderThickness = new Thickness(1.5), IsHitTestVisible = false });
        if (model.Tool != AnnotationTool.StickyNote) // the note has a fixed size
        {
            var grip = new Border
            {
                Width = 10, Height = 10, Background = accent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            };
            ToolTip.SetTip(grip, Localizer.Instance["Annotate.Pending.Resize"]);
            WireResize(grip);
            panel.Children.Add(grip);
        }
        Child = panel;
        WireMove(this);

        _toolbar = BuildToolbar(accent);
        KeyDown += (_, e) => HandleKey(e);
    }

    /// <summary>True when <paramref name="source"/> is this box, its toolbar or anything inside them.</summary>
    public bool Owns(object? source) =>
        source is Visual v && (ReferenceEquals(v, this) || ReferenceEquals(v, _toolbar)
                               || this.IsVisualAncestorOf(v) || _toolbar.IsVisualAncestorOf(v));

    public void Attach()
    {
        _canvas.Children.Add(this);
        _canvas.Children.Add(_toolbar);
        PlaceToolbar();
        Dispatcher.UIThread.Post(() => { if (_editor is not null) _editor.Focus(); else Focus(); }, DispatcherPriority.Loaded);
    }

    public void Detach()
    {
        _canvas.Children.Remove(this);
        _canvas.Children.Remove(_toolbar);
    }

    /// <summary>Burns the annotation in where the box now sits. Safe to call twice; errors go to the status bar.</summary>
    public async Task CommitAsync()
    {
        if (_done) return;
        _done = true;
        try
        {
            await _vm.CommitPendingAsync(Snapshot());
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = Localizer.Instance.Format("Status.Error", ex.Message);
        }
    }

    public void Cancel()
    {
        if (_done) return;
        _done = true;
        _vm.CancelPending();
    }

    // ── Model ─────────────────────────────────────────────────────────────────

    private AnnotationInputModel Snapshot()
    {
        static double C(double v) => Math.Clamp(v, 0.0, 1.0);
        var (l, t) = (Canvas.GetLeft(this), Canvas.GetTop(this));
        var rect = new PdfRect(C(l / _canvasW), C(t / _canvasH), C((l + Width) / _canvasW), C((t + Height) / _canvasH));
        var m = _model with { Rect = rect };
        if (_editor is null) return m;
        return m with
        {
            Text = _editor.Text ?? string.Empty,
            FontSizePoints = Number(_size, m.FontSizePoints),
            CharPitchPoints = Number(_pitch, m.CharPitchPoints),
            ColorHex = NormalizeHex(_color?.Text, m.ColorHex),
            Background = _background?.IsChecked == true,
        };
    }

    private static double Number(NumericUpDown? box, double fallback) => box is { Value: { } d } ? (double)d : fallback;

    private static string NormalizeHex(string? value, string fallback)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return fallback;
        if (!s.StartsWith('#')) s = "#" + s;
        return s.Length is 4 or 7 && Color.TryParse(s, out _) ? s : fallback;
    }

    // ── Content per tool ──────────────────────────────────────────────────────

    private Control BuildContent(AnnotationInputModel m) => m.Tool switch
    {
        AnnotationTool.Highlight => new Border { Background = new SolidColorBrush(Parse(m.ColorHex, Colors.Yellow)) { Opacity = m.Opacity } },
        AnnotationTool.Rectangle => new Border
        {
            BorderBrush = new SolidColorBrush(Parse(m.ColorHex, Colors.Red)),
            BorderThickness = new Thickness(Math.Max(1, m.LineWidth * _scale)),
            Background = m.FillHex is null ? null : new SolidColorBrush(Parse(m.FillHex, Colors.White)),
        },
        AnnotationTool.StickyNote => new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 255, 255, 130)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 160, 0)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = m.Text, FontSize = Math.Max(6, 8 * _scale), FontFamily = TextFamily, Foreground = Brushes.Black,
                Margin = new Thickness(3 * _scale), TextWrapping = TextWrapping.Wrap,
            },
        },
        AnnotationTool.ImageOverlay or AnnotationTool.Signature when m.ImageBytes is not null =>
            new Image { Source = new Bitmap(new MemoryStream(m.ImageBytes)), Stretch = Stretch.Fill },
        AnnotationTool.FreeText => BuildEditor(m),
        _ => new Panel(),
    };

    private TextBox BuildEditor(AnnotationInputModel m)
    {
        _editor = new TextBox
        {
            Text = m.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            BorderThickness = new Thickness(0),
            MinHeight = 0,
            MinWidth = 0,
            CornerRadius = new CornerRadius(0),
            VerticalContentAlignment = VerticalAlignment.Top,
            PlaceholderText = Localizer.Instance["Annotate.Pending.Placeholder"],
            Cursor = new Cursor(StandardCursorType.Ibeam),
        };
        // Enter commits (Shift+Enter for a new line), Esc cancels — before the TextBox sees the key.
        _editor.AddHandler(KeyDownEvent, (_, e) => HandleKey(e), RoutingStrategies.Tunnel);
        return _editor;
    }

    /// <summary>Keeps the editor looking like the burned-in text: same size, colour, background and cell pitch.</summary>
    private void ApplyTextStyle()
    {
        if (_editor is null) return;
        var fontPx = Math.Max(4, Number(_size, _model.FontSizePoints) * _scale);
        var pitchPx = Number(_pitch, _model.CharPitchPoints) * _scale;
        var pad = 3 * _scale;
        _editor.FontSize = fontPx;
        var fg = new SolidColorBrush(Parse(NormalizeHex(_color?.Text, _model.ColorHex), Colors.Black));
        IBrush bg = _background?.IsChecked == true ? new SolidColorBrush(Color.FromArgb(220, 255, 255, 240)) : Brushes.Transparent;
        _editor.Foreground = fg;
        _editor.Background = bg;
        // The Fluent template swaps in its own (opaque, theme-coloured) brushes on hover and focus: pin them all.
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            _editor.Resources[key] = bg;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            _editor.Resources[key] = fg;
        foreach (var key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _editor.Resources[key] = Brushes.Transparent;
        if (pitchPx > 0)
        {
            // ponytail: the PDF centres each Arial glyph in its cell; the preview approximates that with a monospace
            // face spaced to the same pitch, so the cells line up even if the glyph shapes differ a little.
            _editor.FontFamily = CellFamily;
            var advance = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(CellFamily), fontPx, Brushes.Black).WidthIncludingTrailingWhitespace;
            _editor.LetterSpacing = pitchPx - advance;
            _editor.Padding = new Thickness(pad + Math.Max(0, (pitchPx - advance) / 2), pad, 0, 0);
        }
        else
        {
            _editor.FontFamily = TextFamily;
            _editor.LetterSpacing = 0;
            _editor.Padding = new Thickness(pad, pad, 0, 0);
        }
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private Border BuildToolbar(IBrush accent)
    {
        var loc = Localizer.Instance;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var grip = new Border
        {
            Width = 16, Height = 30, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new TextBlock { Text = "⠿", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
        };
        ToolTip.SetTip(grip, loc["Annotate.Pending.Move"]);
        WireMove(grip);
        row.Children.Add(grip);

        if (_model.Tool == AnnotationTool.FreeText)
        {
            _size = new NumericUpDown { Minimum = 4, Maximum = 200, Increment = 1, Value = (decimal)_model.FontSizePoints, FormatString = "0", Width = 54, Height = 30, ShowButtonSpinner = false };
            ToolTip.SetTip(_size, loc["Annotate.Pending.FontSize"]);
            _pitch = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 0.5m, Value = (decimal)_model.CharPitchPoints, FormatString = "0.0", Width = 60, Height = 30, ShowButtonSpinner = false };
            ToolTip.SetTip(_pitch, loc["Annotate.Pending.Spacing"]);
            _color = new TextBox { Text = _model.ColorHex, Width = 84, Height = 30, PlaceholderText = "#RRGGBB" };
            ToolTip.SetTip(_color, loc["Annotate.Pending.Color"]);
            _background = new CheckBox { Content = loc["Annotate.Pending.Background"], IsChecked = _model.Background, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(_background, loc["Annotate.Pending.Background.Tooltip"]);

            _size.ValueChanged += (_, _) => ApplyTextStyle();
            _pitch.ValueChanged += (_, _) => ApplyTextStyle();
            _color.TextChanged += (_, _) => ApplyTextStyle();
            _background.IsCheckedChanged += (_, _) => ApplyTextStyle();
            foreach (var c in new Control[] { _size, _pitch, _color })
                c.AddHandler(KeyDownEvent, (_, e) => { if (e.Key is Key.Escape) HandleKey(e); }, RoutingStrategies.Tunnel);
            row.Children.Add(_size);
            row.Children.Add(_pitch);
            row.Children.Add(_color);
            row.Children.Add(_background);
            ApplyTextStyle();
        }

        if (_editor is not null && _vm.IsEditingExistingFreeText)
            row.Children.Add(Glyph("M3 5h10M6 5V3h4v2M5 5l.7 9h4.6L11 5", loc["Annotate.Pending.Delete"], () => { _editor.Text = string.Empty; _ = CommitAsync(); }, null));
        row.Children.Add(Glyph("M3 8l3.5 3.5L13 4", loc["Annotate.Pending.Apply"], () => _ = CommitAsync(), accent));
        row.Children.Add(Glyph("M4 4l8 8 M12 4l-8 8", loc["Annotate.Pending.Cancel"], Cancel, null));

        var bar = new Border { Padding = new Thickness(4), Child = row };
        bar.Classes.Add("floating");
        return bar;
    }

    private static Button Glyph(string path, string tip, Action onClick, IBrush? stroke)
    {
        var icon = new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse(path), Width = 14, Height = 14, StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Stretch = Stretch.Uniform };
        icon.Classes.Add("glyph");
        if (stroke is not null) icon.Stroke = stroke;
        var btn = new Button { Content = icon };
        btn.Classes.Add("fw");
        ToolTip.SetTip(btn, tip);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>Toolbar above the box (below it when the box touches the top of the page), kept inside the page width.</summary>
    private void PlaceToolbar()
    {
        var (l, t) = (Canvas.GetLeft(this), Canvas.GetTop(this));
        var top = t - ToolbarHeight - ToolbarGap;
        if (top < 0) top = t + Height + ToolbarGap;
        _toolbar.Measure(Size.Infinity);
        var width = _toolbar.Bounds.Width > 0 ? _toolbar.Bounds.Width : _toolbar.DesiredSize.Width;
        Canvas.SetLeft(_toolbar, Math.Max(0, Math.Min(l, _canvasW - width)));
        Canvas.SetTop(_toolbar, top);
    }

    // ── Keys ──────────────────────────────────────────────────────────────────

    private void HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Cancel();
                break;
            case Key.Enter or Key.Return when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                _ = CommitAsync();
                break;
        }
    }

    // ── Move / resize ─────────────────────────────────────────────────────────

    private void WireMove(Control handle)
    {
        Point start = default, origin = default;
        var dragging = false;
        handle.PointerPressed += (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (_editor is null) Focus();
            dragging = true;
            start = e.GetPosition(_canvas);
            origin = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            e.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var d = e.GetPosition(_canvas) - start;
            Canvas.SetLeft(this, Math.Clamp(origin.X + d.X, 0, Math.Max(0, _canvasW - Width)));
            Canvas.SetTop(this, Math.Clamp(origin.Y + d.Y, 0, Math.Max(0, _canvasH - Height)));
            PlaceToolbar();
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
    }

    private void WireResize(Control grip)
    {
        Point start = default;
        Size origin = default;
        var dragging = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            dragging = true;
            start = e.GetPosition(_canvas);
            origin = new Size(Width, Height);
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var d = e.GetPosition(_canvas) - start;
            var (l, t) = (Canvas.GetLeft(this), Canvas.GetTop(this));
            Width = Math.Clamp(origin.Width + d.X, MinSize, Math.Max(MinSize, _canvasW - l));
            Height = Math.Clamp(origin.Height + d.Y, MinSize, Math.Max(MinSize, _canvasH - t));
            PlaceToolbar();
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Color Parse(string hex, Color fallback) => Color.TryParse(hex, out var c) ? c : fallback;

    private static IBrush? Resource(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as IBrush : null;
}
