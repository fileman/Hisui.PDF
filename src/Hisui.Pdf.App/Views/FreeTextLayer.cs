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
/// While the Annotate panel is open (or the free-text tool is active): a box over each free text of the page.
/// Drag moves it, click opens it for editing (text, size, spacing, position), Del deletes it.
/// Boxes are kept by text id across reloads, so the one being worked on keeps the keyboard focus (Ctrl+Z, Del).
/// </summary>
public sealed class FreeTextLayer : Canvas
{
    public static readonly StyledProperty<PageItemViewModel?> PageProperty =
        AvaloniaProperty.Register<FreeTextLayer, PageItemViewModel?>(nameof(Page));

    public PageItemViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x5F, 0xB8));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x5F, 0xB8));
    private MainViewModel? _vm;
    private PageItemViewModel? _page;
    private Size _builtFor;
    private readonly Dictionary<string, Border> _boxes = [];

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
        if (e.PropertyName == nameof(PageItemViewModel.FreeTexts)) Rebuild();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsFreeTextLayerVisible) or nameof(MainViewModel.Pending)
            or nameof(MainViewModel.IsAnnotationActive))
            Rebuild();
    }

    /// <summary>Brings the boxes in line with the page's texts: existing ones are moved, not recreated.</summary>
    private void Rebuild()
    {
        _builtFor = Bounds.Size;
        var (w, h) = (Bounds.Width, Bounds.Height);
        var texts = _vm is { IsFreeTextLayerVisible: true, Pending: null } && Page is { } page && w > 0 && h > 0
            ? page.FreeTexts : [];
        // With a tool active the annotation canvas sits on top and takes the pointer; the outlines still show.
        IsHitTestVisible = _vm is { IsAnnotationActive: false };

        foreach (var id in _boxes.Keys.Except(texts.Select(t => t.Id)).ToList())
        {
            Children.Remove(_boxes[id]);
            _boxes.Remove(id);
        }
        foreach (var info in texts)
        {
            if (!_boxes.TryGetValue(info.Id, out var box))
            {
                box = CreateBox();
                _boxes[info.Id] = box;
                Children.Add(box);
            }
            box.Tag = info;
            var r = info.Rect;
            box.Width = Math.Max(8, r.Width * w);
            box.Height = Math.Max(8, r.Height * h);
            SetLeft(box, r.Left * w);
            SetTop(box, r.Top * h);
        }
    }

    private Border CreateBox()
    {
        var box = new Border
        {
            Background = Fill,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            Focusable = true,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        // No tooltip: its popup would open under the pointer and take the click meant for the box.

        Point start = default;
        Point origin = default;
        var dragging = false;
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            box.Focus();
            e.Handled = true;
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
        };
        box.PointerReleased += async (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            if (_vm is not { } vm || Page is not { } page || box.Tag is not FreeTextInfo info) return;
            var (w, h) = (Bounds.Width, Bounds.Height);
            var (dx, dy) = ((GetLeft(box) - origin.X) / w, (GetTop(box) - origin.Y) / h);
            if (Math.Abs(dx * w) >= 2 || Math.Abs(dy * h) >= 2) await vm.MoveFreeTextAsync(page, info, dx, dy);
            else await vm.BeginEditFreeTextAsync(page, info); // a click: open it for editing
        };
        box.KeyDown += async (_, e) =>
        {
            if (e.Key is not (Key.Delete or Key.Back)) return;
            e.Handled = true;
            if (_vm is { } vm && Page is { } page && box.Tag is FreeTextInfo info) await vm.DeleteFreeTextAsync(page, info);
        };
        return box;
    }
}
