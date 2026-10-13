using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// Form-field overlay for one page, sized to the page image. Fill mode: an editor per widget. Layout mode (Forms panel,
/// no tool): a box per widget — click/Ctrl+click selects, dragging on the empty page draws a selection rectangle
/// (Ctrl/Shift adds), Ctrl+A selects all, drag moves the selection, the right/bottom edge resizes, arrows nudge
/// (Shift = 10 px), Del deletes, double-click opens the properties.
/// </summary>
public sealed class FieldLayer : Canvas
{
    public static readonly StyledProperty<PageItemViewModel?> PageProperty =
        AvaloniaProperty.Register<FieldLayer, PageItemViewModel?>(nameof(Page));

    public PageItemViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private const double MinFieldPx = 4;
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0xB8));
    private static readonly IBrush BoxFill = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x5F, 0xB8));
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x5F, 0xB8));
    private static readonly IBrush RequiredBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));

    private MainViewModel? _vm;
    private PageItemViewModel? _subscribedPage;
    private readonly Dictionary<int, Border> _boxes = [];
    private Size _builtFor;
    private Point? _marqueeStart;
    private Rectangle? _marquee;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = this.FindAncestorOfType<DocumentView>()?.DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Subscribe(Page);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        Subscribe(null);
        _vm = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) { Subscribe(Page); Rebuild(); }
        else if (change.Property == BoundsProperty && Bounds.Size != _builtFor) Rebuild();
    }

    private void Subscribe(PageItemViewModel? page)
    {
        if (_subscribedPage is not null) _subscribedPage.PropertyChanged -= OnPageChanged;
        _subscribedPage = page;
        if (page is not null) page.PropertyChanged += OnPageChanged;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageItemViewModel.Fields)) Rebuild();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsFormLayoutMode) or nameof(MainViewModel.IsAnnotationActive) or nameof(MainViewModel.ShowTabOrder):
                Rebuild();
                break;
            case nameof(MainViewModel.SelectedFields) or nameof(MainViewModel.AlignAnchor):
                UpdateSelectionVisuals();
                break;
        }
    }

    private void Rebuild()
    {
        var hadFocus = Children.Any(c => c.IsKeyboardFocusWithin);
        Children.Clear();
        _boxes.Clear();
        _marquee = null;
        _marqueeStart = null;
        _builtFor = Bounds.Size;
        Background = null;
        if (_vm is not { IsAnnotationActive: false } vm || Page is not { } page) return;
        if (vm.IsFormLayoutMode) Background = Brushes.Transparent; // the empty page takes the selection rectangle
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0) return;

        var order = 0;
        foreach (var field in page.Fields)
        {
            order++;
            var control = vm.IsFormLayoutMode ? LayoutBox(vm, page, field, w, h, order) : Editor(vm, page, field, h);
            if (control is null) continue;
            SetLeft(control, field.Rect.Left * w);
            SetTop(control, field.Rect.Top * h);
            control.Width = Math.Max(MinFieldPx, field.Rect.Width * w);
            control.Height = Math.Max(MinFieldPx, field.Rect.Height * h);
            if (!vm.IsFormLayoutMode) ToolTip.SetTip(control, field.Tooltip ?? field.Name);
            Children.Add(control);
        }
        UpdateSelectionVisuals();
        // Keep the keyboard on the anchor after a layout edit rebuilt the boxes (arrows, Del keep working).
        if (hadFocus && vm.IsFormLayoutMode && ReferenceEquals(vm.SelectionPage, page) && vm.AlignAnchor is { } anchorIndex &&
            _boxes.TryGetValue(anchorIndex, out var anchor))
            Dispatcher.UIThread.Post(() => anchor.Focus(), DispatcherPriority.Loaded);
    }

    private void UpdateSelectionVisuals()
    {
        if (_vm is null) return;
        var selected = ReferenceEquals(_vm.SelectionPage, Page) ? _vm.SelectedFields : [];
        foreach (var (index, box) in _boxes)
        {
            var on = selected.Contains(index);
            box.Background = on ? SelectedFill : BoxFill;
            box.BorderThickness = new Thickness(on ? (_vm.AlignAnchor == index ? 2.5 : 2) : 1);
        }
    }

    // ── Selection rectangle (layout mode, pressed on the empty page) ──────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || _vm is not { IsFormLayoutMode: true } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _marqueeStart = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_marqueeStart is not { } a) return;
        if (_marquee is null)
        {
            _marquee = new Rectangle { Stroke = Accent, StrokeThickness = 1, StrokeDashArray = [3, 2], Fill = BoxFill, IsHitTestVisible = false };
            Children.Add(_marquee);
        }
        var r = new Rect(a, e.GetPosition(this)).Normalize();
        SetLeft(_marquee, r.X);
        SetTop(_marquee, r.Y);
        _marquee.Width = r.Width;
        _marquee.Height = r.Height;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_marqueeStart is not { } a) return;
        _marqueeStart = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        var add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (_marquee is null) // a click on the empty page
        {
            if (!add) _vm?.ClearFieldSelection();
            return;
        }
        var r = new Rect(a, e.GetPosition(this)).Normalize();
        Children.Remove(_marquee);
        _marquee = null;
        if (_vm is null || Page is null) return;
        var hit = _boxes.Where(kv => new Rect(GetLeft(kv.Value), GetTop(kv.Value), kv.Value.Width, kv.Value.Height).Intersects(r))
            .Select(kv => kv.Key);
        _vm.SelectFields(Page, hit, add);
    }

    // ── Fill ──────────────────────────────────────────────────────────────────

    private static Control? Editor(MainViewModel vm, PageItemViewModel page, PdfFormWidget field, double pageH)
    {
        var fontSize = Math.Clamp(field.Rect.Height * pageH * 0.62, 5, 16);
        switch (field.Type)
        {
            case PdfFormFieldType.Text:
                var shown = FieldValueFormatter.ToDisplay(field.Format, field.Decimals, field.Value);
                var box = new TextBox
                {
                    Classes = { "field" },
                    Text = shown,
                    IsReadOnly = field.IsReadOnly,
                    FontSize = field.IsMultiline ? Math.Min(fontSize, 11) : fontSize,
                    Foreground = Brushes.Black,
                    CaretBrush = Brushes.Black,
                    Padding = new Thickness(2, 0),
                    MinHeight = 0,
                    MinWidth = 0,
                    MaxLength = field.MaxLength ?? 0,
                    AcceptsReturn = field.IsMultiline,
                    TextWrapping = field.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    VerticalContentAlignment = field.IsMultiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                    TextAlignment = field.Alignment switch { FieldAlignment.Center => TextAlignment.Center, FieldAlignment.Right => TextAlignment.Right, _ => TextAlignment.Left },
                };
                void MarkRequired() => box.Classes.Set("required", field.IsRequired && string.IsNullOrWhiteSpace(box.Text));
                MarkRequired();
                var editing = false;
                // Formatted fields show the formatted value; editing switches to the raw value, leaving parses it back.
                box.GotFocus += (_, _) =>
                {
                    if (field.Format is FieldFormat.None or FieldFormat.Date) return;
                    editing = true;
                    box.Text = FieldValueFormatter.ToDisplay(field.Format, field.Decimals, field.Value).Replace("€", "").Replace("%", "").Trim();
                    editing = false;
                };
                box.TextChanged += (_, _) =>
                {
                    MarkRequired();
                    if (editing) return;
                    var raw = FieldValueFormatter.ToRaw(field.Format, field.DatePattern, box.Text ?? "");
                    box.Classes.Set("invalid", raw is null);
                    vm.SetPendingFieldEdit(page, field, raw ?? box.Text ?? "");
                };
                box.LostFocus += async (_, _) =>
                {
                    await vm.FlushFieldEditAsync();
                    if (field.Format == FieldFormat.None || box.Classes.Contains("invalid")) return;
                    var raw = FieldValueFormatter.ToRaw(field.Format, field.DatePattern, box.Text ?? "");
                    editing = true;
                    box.Text = FieldValueFormatter.ToDisplay(field.Format, field.Decimals, raw);
                    editing = false;
                };
                box.KeyDown += async (_, e) =>
                {
                    if (e.Key != Key.Enter || field.IsMultiline) return;
                    e.Handled = true;
                    await vm.FlushFieldEditAsync();
                };
                return box;

            case PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton:
                var isOn = field.Type == PdfFormFieldType.CheckBox ? field.Value == "true" : field.Value == field.OnState && field.Value is not null;
                var toggle = new ToggleButton
                {
                    Classes = { "field" },
                    IsChecked = isOn,
                    IsEnabled = !field.IsReadOnly,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Content = new TextBlock
                    {
                        Text = field.Type == PdfFormFieldType.CheckBox ? "✓" : "●",
                        FontSize = fontSize,
                        Foreground = Brushes.Black,
                        IsVisible = isOn,
                    },
                };
                toggle.IsCheckedChanged += async (_, _) =>
                {
                    var on = toggle.IsChecked == true;
                    if (field.Type == PdfFormFieldType.RadioButton && !on) { toggle.IsChecked = true; return; } // pick another choice instead
                    ((TextBlock)toggle.Content!).IsVisible = on;
                    var value = field.Type == PdfFormFieldType.CheckBox ? (on ? "true" : "false") : field.OnState ?? "";
                    await vm.SetFieldValueAsync(page, field, value);
                };
                return toggle;

            case PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox:
                var combo = new ComboBox
                {
                    ItemsSource = field.Options,
                    SelectedItem = field.Value,
                    IsEnabled = !field.IsReadOnly,
                    FontSize = fontSize,
                    Padding = new Thickness(2, 0),
                    MinHeight = 0,
                    MinWidth = 0,
                };
                combo.SelectionChanged += async (_, _) =>
                {
                    if (combo.SelectedItem is string value) await vm.SetFieldValueAsync(page, field, value);
                };
                return combo;

            default: // signature fields are signed with the Firma tool, not typed into
                return null;
        }
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    [Flags]
    private enum DragMode { None = 0, Move = 1, Right = 2, Bottom = 4 }

    private DispatcherTimer? _nudgeTimer;

    private Control LayoutBox(MainViewModel vm, PageItemViewModel page, PdfFormWidget field, double pageW, double pageH, int order)
    {
        var box = new Border
        {
            Background = BoxFill,
            BorderBrush = field.IsRequired ? RequiredBrush : Accent,
            BorderThickness = new Thickness(1),
            Focusable = true,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        if (vm.ShowTabOrder)
            box.Child = new Border
            {
                Background = Accent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = new TextBlock { Text = order.ToString(), FontSize = 9, Foreground = Brushes.White },
            };
        ToolTip.SetTip(box, $"{field.Tooltip ?? field.Name}\n{Localizer.Instance["Fields.LayoutHint"]}");
        _boxes[field.AnnotIndex] = box;

        var mode = DragMode.None;
        Point start = default;
        var origins = new Dictionary<int, Rect>();

        DragMode ZoneAt(Point p) // p in box coordinates
        {
            var zx = Math.Min(6, box.Bounds.Width / 4);
            var zy = Math.Min(6, box.Bounds.Height / 4);
            var m = DragMode.None;
            if (p.X >= box.Bounds.Width - zx) m |= DragMode.Right;
            if (p.Y >= box.Bounds.Height - zy) m |= DragMode.Bottom;
            return m == DragMode.None ? DragMode.Move : m;
        }

        // The boxes a drag or nudge moves: the selection (when this box is in it), else this box alone.
        Dictionary<int, Border> Movers() =>
            ReferenceEquals(vm.SelectionPage, page) && vm.SelectedFields.Contains(field.AnnotIndex)
                ? vm.SelectedFields.Where(_boxes.ContainsKey).ToDictionary(i => i, i => _boxes[i])
                : new Dictionary<int, Border> { [field.AnnotIndex] = box };

        static Rect RectOf(Border b) => new(GetLeft(b), GetTop(b), b.Width, b.Height);
        PdfRect Normalized(Border b) => new(GetLeft(b) / pageW, GetTop(b) / pageH, (GetLeft(b) + b.Width) / pageW, (GetTop(b) + b.Height) / pageH);

        box.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(box);
            if (!point.Properties.IsLeftButtonPressed) return;
            box.Focus();
            e.Handled = true;
            var toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (toggle) { vm.SelectField(page, field.AnnotIndex, toggle: true); return; }
            if (!(ReferenceEquals(vm.SelectionPage, page) && vm.SelectedFields.Contains(field.AnnotIndex)))
                vm.SelectField(page, field.AnnotIndex, toggle: false);
            if (e.ClickCount == 2) { vm.EditFieldPropertiesCommand.Execute(null); return; }

            mode = ZoneAt(e.GetPosition(box));
            start = e.GetPosition(this);
            origins = (mode == DragMode.Move ? Movers() : new Dictionary<int, Border> { [field.AnnotIndex] = box })
                .ToDictionary(kv => kv.Key, kv => RectOf(kv.Value));
            e.Pointer.Capture(box);
        };
        box.PointerMoved += (_, e) =>
        {
            if (mode == DragMode.None)
            {
                box.Cursor = new Cursor(ZoneAt(e.GetPosition(box)) switch
                {
                    DragMode.Right => StandardCursorType.SizeWestEast,
                    DragMode.Bottom => StandardCursorType.SizeNorthSouth,
                    DragMode.Right | DragMode.Bottom => StandardCursorType.BottomRightCorner,
                    _ => StandardCursorType.SizeAll,
                });
                return;
            }
            var d = e.GetPosition(this) - start;
            if (mode == DragMode.Move)
            {
                // The group moves as one, clamped so no member leaves the page.
                var dx = origins.Values.Aggregate(d.X, (acc, o) => Math.Clamp(acc, -o.X, pageW - o.Right));
                var dy = origins.Values.Aggregate(d.Y, (acc, o) => Math.Clamp(acc, -o.Y, pageH - o.Bottom));
                foreach (var (index, o) in origins)
                {
                    SetLeft(_boxes[index], o.X + dx);
                    SetTop(_boxes[index], o.Y + dy);
                }
            }
            var origin = origins[field.AnnotIndex];
            if (mode.HasFlag(DragMode.Right)) box.Width = Math.Clamp(origin.Width + d.X, MinFieldPx, Math.Max(MinFieldPx, pageW - origin.X));
            if (mode.HasFlag(DragMode.Bottom)) box.Height = Math.Clamp(origin.Height + d.Y, MinFieldPx, Math.Max(MinFieldPx, pageH - origin.Y));
            e.Handled = true;
        };
        box.PointerReleased += async (_, e) =>
        {
            if (mode == DragMode.None) return;
            mode = DragMode.None;
            e.Pointer.Capture(null);
            e.Handled = true;
            var moved = origins.Where(kv => (RectOf(_boxes[kv.Key]).TopLeft - kv.Value.TopLeft) is var v && (Math.Abs(v.X) >= 0.5 || Math.Abs(v.Y) >= 0.5)
                                            || Math.Abs(_boxes[kv.Key].Width - kv.Value.Width) >= 0.5 || Math.Abs(_boxes[kv.Key].Height - kv.Value.Height) >= 0.5)
                .ToDictionary(kv => kv.Key, kv => Normalized(_boxes[kv.Key]));
            await vm.MoveFieldsAsync(page, moved); // a click (nothing moved) commits nothing
        };
        box.KeyDown += (_, e) =>
        {
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            var (dx, dy) = e.Key switch
            {
                Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0),
            };
            if (dx != 0 || dy != 0)
            {
                // Nudges move the boxes at once and write the PDF once the keys rest (one undo step per burst).
                var movers = Movers();
                foreach (var b in movers.Values)
                {
                    SetLeft(b, Math.Clamp(GetLeft(b) + dx, 0, Math.Max(0, pageW - b.Width)));
                    SetTop(b, Math.Clamp(GetTop(b) + dy, 0, Math.Max(0, pageH - b.Height)));
                }
                _nudgeTimer?.Stop();
                _nudgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                _nudgeTimer.Tick += async (_, _) =>
                {
                    _nudgeTimer?.Stop();
                    await vm.MoveFieldsAsync(page, movers.ToDictionary(kv => kv.Key, kv => Normalized(kv.Value)));
                };
                _nudgeTimer.Start();
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.Delete or Key.Back:
                    if (!vm.SelectedFields.Contains(field.AnnotIndex)) vm.SelectField(page, field.AnnotIndex, toggle: false);
                    vm.DeleteSelectedFieldsCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                    vm.SelectAllFields(page);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    vm.ClearFieldSelection();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    vm.EditFieldPropertiesCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        };
        return box;
    }
}
