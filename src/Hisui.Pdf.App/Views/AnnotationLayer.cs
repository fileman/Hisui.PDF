using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// While the Annotate panel is open (or the free-text tool is active): a quiet box over each annotation placed on the
/// page, invisible until hovered (thin outline) or selected (outline + resize handles). Click selects, drag moves,
/// the handles resize, double-click or Enter opens it for editing, Del deletes, arrows nudge by 1 pt (Shift: 10),
/// Esc deselects. Boxes are kept by id across reloads, so the one being worked on keeps the keyboard focus.
/// </summary>
public sealed class AnnotationLayer : Canvas
{
    public static readonly StyledProperty<PageItemViewModel?> PageProperty =
        AvaloniaProperty.Register<AnnotationLayer, PageItemViewModel?>(nameof(Page));

    public PageItemViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0xB8));
    private static readonly IBrush HoverStroke = new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0x5F, 0xB8));
    private const double HandleSize = 7, MinBoxPx = 8;
    // Handle anchors: -1/0/1 on each axis, the corners and the edge midpoints.
    private static readonly (int X, int Y)[] Anchors = [(-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0)];

    private MainViewModel? _vm;
    private PageItemViewModel? _page;
    private Size _builtFor;
    private readonly Dictionary<string, Border> _boxes = [];
    private readonly Border[] _handles = new Border[8];
    private string? _selectedId;

    public AnnotationLayer()
    {
        for (var i = 0; i < Anchors.Length; i++) _handles[i] = CreateHandle(Anchors[i]);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = this.FindAncestorOfType<DocumentView>()?.DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Watch(Page);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = null;
        Watch(null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) { Watch(Page); Rebuild(); }
        else if (change.Property == BoundsProperty && Bounds.Size != _builtFor) Rebuild();
    }

    private void Watch(PageItemViewModel? page)
    {
        if (ReferenceEquals(_page, page)) return;
        if (_page is not null) _page.PropertyChanged -= OnPageChanged;
        _page = page;
        if (_page is not null) _page.PropertyChanged += OnPageChanged;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageItemViewModel.Placed)) Rebuild();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsPlacedLayerVisible) or nameof(MainViewModel.Pending)
            or nameof(MainViewModel.IsAnnotationActive))
            Rebuild();
        else if (e.PropertyName == nameof(MainViewModel.SelectedPlacedId)) SyncSelection(); // chosen in the list
    }

    /// <summary>Brings the boxes in line with the page's annotations: existing ones are moved, not recreated.</summary>
    private void Rebuild()
    {
        _builtFor = Bounds.Size;
        var (w, h) = (Bounds.Width, Bounds.Height);
        var placed = _vm is { IsPlacedLayerVisible: true, Pending: null } && Page is { } page && w > 0 && h > 0
            ? page.Placed : [];
        // With a tool active the annotation canvas sits on top and takes the pointer.
        IsHitTestVisible = _vm is { IsAnnotationActive: false };

        foreach (var id in _boxes.Keys.Except(placed.Select(a => a.Id)).ToList())
        {
            Children.Remove(_boxes[id]);
            _boxes.Remove(id);
        }
        foreach (var a in placed)
        {
            if (!_boxes.TryGetValue(a.Id, out var box))
            {
                box = CreateBox();
                _boxes[a.Id] = box;
                Children.Add(box);
            }
            box.Tag = a;
            var r = a.Rect;
            box.Width = Math.Max(MinBoxPx, r.Width * w);
            box.Height = Math.Max(MinBoxPx, r.Height * h);
            SetLeft(box, r.Left * w);
            SetTop(box, r.Top * h);
        }
        SyncSelection(); // the selection does not survive a reload that dropped its annotation
    }

    /// <summary>Follows the view model's selection (set by the list or by another page's layer); never writes it back.</summary>
    private void SyncSelection() =>
        Select(_vm?.SelectedPlacedId is { } sel && _boxes.ContainsKey(sel) ? sel : null);

    /// <summary>The user chose a box (or none): publish it so the list follows. The compare stops any echo.</summary>
    private void UserSelect(string? id)
    {
        Select(id);
        if (_vm is not null && _vm.SelectedPlacedId != id) _vm.SelectedPlacedId = id;
    }

    private Border? Selected => _selectedId is { } id && _boxes.TryGetValue(id, out var box) ? box : null;

    private void Select(string? id)
    {
        if (Selected is { } old) { old.BorderBrush = null; old.BorderThickness = new Thickness(1); }
        _selectedId = id;
        if (Selected is { } box)
        {
            box.BorderBrush = Accent;
            box.BorderThickness = new Thickness(1.5);
            foreach (var handle in _handles) if (!Children.Contains(handle)) Children.Add(handle);
            PlaceHandles(box);
        }
        else
        {
            foreach (var handle in _handles) Children.Remove(handle);
        }
    }

    private void PlaceHandles(Border box)
    {
        var (cx, cy) = (GetLeft(box) + box.Width / 2, GetTop(box) + box.Height / 2);
        for (var i = 0; i < Anchors.Length; i++)
        {
            SetLeft(_handles[i], cx + Anchors[i].X * box.Width / 2 - HandleSize / 2);
            SetTop(_handles[i], cy + Anchors[i].Y * box.Height / 2 - HandleSize / 2);
        }
    }

    private PdfRect Normalized(Border b) =>
        new(GetLeft(b) / Bounds.Width, GetTop(b) / Bounds.Height, (GetLeft(b) + b.Width) / Bounds.Width, (GetTop(b) + b.Height) / Bounds.Height);

    private Border CreateBox()
    {
        var box = new Border
        {
            Background = Brushes.Transparent, // hit-testable, nothing drawn
            BorderThickness = new Thickness(1),
            Focusable = true,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        // No tooltip: its popup would open under the pointer and take the click meant for the box.
        string Id() => ((PlacedAnnotation)box.Tag!).Id;

        box.PointerEntered += (_, _) => { if (_selectedId != Id()) box.BorderBrush = HoverStroke; };
        box.PointerExited += (_, _) => { if (_selectedId != Id()) box.BorderBrush = null; };

        Point start = default;
        Point origin = default;
        var dragging = false;
        box.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            UserSelect(Id());
            box.Focus();
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                if (_vm is { } vm && Page is { } page && box.Tag is PlacedAnnotation a) await vm.BeginEditPlacedAsync(page, a);
                return;
            }
            dragging = true;
            start = e.GetPosition(this);
            origin = new Point(GetLeft(box), GetTop(box));
            e.Pointer.Capture(box);
        };
        box.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var d = e.GetPosition(this) - start;
            SetLeft(box, Math.Clamp(origin.X + d.X, 0, Math.Max(0, Bounds.Width - box.Width)));
            SetTop(box, Math.Clamp(origin.Y + d.Y, 0, Math.Max(0, Bounds.Height - box.Height)));
            PlaceHandles(box);
        };
        box.PointerReleased += async (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            if (_vm is not { } vm || Page is not { } page || box.Tag is not PlacedAnnotation a) return;
            var (w, h) = (Bounds.Width, Bounds.Height);
            var (dx, dy) = ((GetLeft(box) - origin.X) / w, (GetTop(box) - origin.Y) / h);
            if (Math.Abs(dx * w) >= 2 || Math.Abs(dy * h) >= 2) await vm.MovePlacedAsync(page, a, dx, dy); // a click just selects
        };
        box.KeyDown += async (_, e) =>
        {
            if (_vm is not { } vm || Page is not { } page || box.Tag is not PlacedAnnotation a) return;
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            var (dx, dy) = e.Key switch
            {
                Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), Key.Down => (0, step), _ => (0, 0),
            };
            if (dx != 0 || dy != 0)
            {
                e.Handled = true;
                // Points to normalised page units at the current zoom (each nudge is one undo step).
                var ppp = vm.PixelsPerPoint;
                await vm.MovePlacedAsync(page, a, dx * ppp / Bounds.Width, dy * ppp / Bounds.Height);
                return;
            }
            switch (e.Key)
            {
                case Key.Delete or Key.Back:
                    e.Handled = true;
                    await vm.DeletePlacedAsync(page, a);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    await vm.BeginEditPlacedAsync(page, a);
                    break;
                case Key.Escape:
                    e.Handled = true;
                    UserSelect(null);
                    break;
            }
        };
        return box;
    }

    /// <summary>A resize handle of the selected box: dragging it moves the box's edges it anchors, live; the new
    /// rectangle is written on release (one undo step).</summary>
    private Border CreateHandle((int X, int Y) anchor)
    {
        var handle = new Border
        {
            Width = HandleSize, Height = HandleSize,
            Background = Brushes.White, BorderBrush = Accent, BorderThickness = new Thickness(1),
            Cursor = new Cursor(anchor switch
            {
                (0, _) => StandardCursorType.SizeNorthSouth,
                (_, 0) => StandardCursorType.SizeWestEast,
                (-1, -1) => StandardCursorType.TopLeftCorner,
                (1, -1) => StandardCursorType.TopRightCorner,
                (-1, 1) => StandardCursorType.BottomLeftCorner,
                _ => StandardCursorType.BottomRightCorner,
            }),
        };
        Point start = default;
        Rect origin = default;
        var dragging = false;
        handle.PointerPressed += (_, e) =>
        {
            if (Selected is not { } box || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            dragging = true;
            start = e.GetPosition(this);
            origin = new Rect(GetLeft(box), GetTop(box), box.Width, box.Height);
            e.Pointer.Capture(handle);
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging || Selected is not { } box) return;
            var d = e.GetPosition(this) - start;
            var (l, t, r, b) = (origin.Left, origin.Top, origin.Right, origin.Bottom);
            if (anchor.X < 0) l = Math.Clamp(l + d.X, 0, r - MinBoxPx);
            if (anchor.X > 0) r = Math.Clamp(r + d.X, l + MinBoxPx, Bounds.Width);
            if (anchor.Y < 0) t = Math.Clamp(t + d.Y, 0, b - MinBoxPx);
            if (anchor.Y > 0) b = Math.Clamp(b + d.Y, t + MinBoxPx, Bounds.Height);
            SetLeft(box, l);
            SetTop(box, t);
            box.Width = r - l;
            box.Height = b - t;
            PlaceHandles(box);
            e.Handled = true;
        };
        handle.PointerReleased += async (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            if (_vm is not { } vm || Page is not { } page || Selected is not { } box || box.Tag is not PlacedAnnotation a) return;
            var now = new Rect(GetLeft(box), GetTop(box), box.Width, box.Height);
            if (Math.Abs(now.Left - origin.Left) >= 0.5 || Math.Abs(now.Top - origin.Top) >= 0.5
                || Math.Abs(now.Width - origin.Width) >= 0.5 || Math.Abs(now.Height - origin.Height) >= 0.5)
                await vm.ResizePlacedAsync(page, a, Normalized(box));
        };
        return handle;
    }
}

/// <summary>Attached <c>DoubleTap.Command</c>: runs the command on a double-click anywhere in the control.</summary>
public static class DoubleTap
{
    public static readonly AttachedProperty<System.Windows.Input.ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, System.Windows.Input.ICommand?>("Command", typeof(DoubleTap));

    public static System.Windows.Input.ICommand? GetCommand(InputElement c) => c.GetValue(CommandProperty);
    public static void SetCommand(InputElement c, System.Windows.Input.ICommand? v) => c.SetValue(CommandProperty, v);

    static DoubleTap() => CommandProperty.Changed.AddClassHandler<InputElement>((c, _) =>
    {
        c.DoubleTapped -= OnDoubleTapped; // idempotent re-subscribe
        if (GetCommand(c) is not null) c.DoubleTapped += OnDoubleTapped;
    });

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is InputElement c && GetCommand(c) is { } cmd && cmd.CanExecute(null)) cmd.Execute(null);
    }
}
