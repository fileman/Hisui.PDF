using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hisui.Pdf.App.Localization;
using Hisui.Pdf.App.ViewModels;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.App.Views;

/// <summary>
/// Selectable text over a page (normal viewing only): drag to select words in reading order, double-click picks a
/// word, Ctrl+C / right-click copies the selection.
/// </summary>
public sealed class TextSelectionLayer : Canvas
{
    public static readonly StyledProperty<PageItemViewModel?> PageProperty =
        AvaloniaProperty.Register<TextSelectionLayer, PageItemViewModel?>(nameof(Page));

    public PageItemViewModel? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly IBrush SelectionFill = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x5F, 0xB8));
    private MainViewModel? _vm;
    private PageItemViewModel? _subscribed;
    private int _anchor = -1, _caret = -1;
    private bool _dragging;

    public TextSelectionLayer()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        var copy = new MenuItem { Header = Localizer.Instance["Text.Copy"] };
        copy.Click += async (_, _) => await CopyAsync();
        ContextMenu = new ContextMenu { Items = { copy } };
        ContextMenu.Opening += (_, e) => { if (SelectedText().Length == 0) e.Cancel = true; };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm = this.FindAncestorOfType<DocumentView>()?.DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Subscribe(Page);
        UpdateInteractive();
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
        if (change.Property == PageProperty) { Subscribe(Page); ClearSelection(); }
        else if (change.Property == BoundsProperty) Redraw();
    }

    private void Subscribe(PageItemViewModel? page)
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnPageChanged;
        _subscribed = page;
        if (page is not null) page.PropertyChanged += OnPageChanged;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageItemViewModel.Words)) ClearSelection();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsAnnotationActive) or nameof(MainViewModel.IsFormLayoutMode) or nameof(MainViewModel.IsCommentsPanel))
            UpdateInteractive();
    }

    private void UpdateInteractive()
    {
        IsHitTestVisible = _vm is { IsAnnotationActive: false, IsFormLayoutMode: false, IsCommentsPanel: false };
        if (!IsHitTestVisible) ClearSelection();
    }

    private IReadOnlyList<TextWord> Words => Page?.Words ?? [];

    private void ClearSelection()
    {
        _anchor = _caret = -1;
        _dragging = false;
        Redraw();
    }

    /// <summary>Index of the word under the point, or the nearest one (so dragging through gaps keeps selecting).</summary>
    private int WordAt(Point p)
    {
        var words = Words;
        if (words.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return -1;
        var (nx, ny) = (p.X / Bounds.Width, p.Y / Bounds.Height);
        int best = -1;
        var bestDist = double.MaxValue;
        for (var i = 0; i < words.Count; i++)
        {
            var b = words[i].BoundingBox;
            var dx = nx < b.Left ? b.Left - nx : nx > b.Right ? nx - b.Right : 0;
            var dy = ny < b.Top ? b.Top - ny : ny > b.Bottom ? ny - b.Bottom : 0;
            // Vertical distance dominates so the pointer stays on its own line.
            var d = dy * 4 + dx;
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var i = WordAt(pt.Position);
        _anchor = _caret = i;
        _dragging = i >= 0;
        if (e.ClickCount == 2 && i >= 0) _dragging = false; // double-click: just that word
        Redraw();
        if (_dragging) e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var i = WordAt(e.GetPosition(this));
        if (i < 0 || i == _caret) return;
        _caret = i;
        Redraw();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C && SelectedText().Length > 0)
        {
            e.Handled = true;
            await CopyAsync();
        }
        else if (e.Key == Key.Escape) ClearSelection();
    }

    private async Task CopyAsync()
    {
        var text = SelectedText();
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    private (int From, int To)? Range()
    {
        var count = Words.Count;
        if (_anchor < 0 || _caret < 0 || _anchor >= count || _caret >= count) return null;
        return (Math.Min(_anchor, _caret), Math.Max(_anchor, _caret));
    }

    private string SelectedText()
    {
        if (Range() is not { } range) return "";
        var (lo, hi) = range;
        var words = Words;
        var sb = new StringBuilder();
        for (var i = lo; i <= hi; i++)
        {
            if (i > lo)
            {
                var (prev, cur) = (words[i - 1].BoundingBox, words[i].BoundingBox);
                // New line when the word starts below the previous word's vertical middle band.
                sb.Append(cur.Top >= (prev.Top + prev.Bottom) / 2 && cur.Left < prev.Right ? '\n' : ' ');
            }
            sb.Append(words[i].Text);
        }
        return sb.ToString();
    }

    private void Redraw()
    {
        Children.Clear();
        if (Range() is not { } range) return;
        var (lo, hi) = range;
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0) return;
        var words = Words;
        for (var i = lo; i <= hi; i++)
        {
            var b = words[i].BoundingBox;
            var rect = new Border
            {
                Background = SelectionFill,
                IsHitTestVisible = false,
                Width = Math.Max(1, b.Width * w),
                Height = Math.Max(1, b.Height * h),
            };
            SetLeft(rect, b.Left * w);
            SetTop(rect, b.Top * h);
            Children.Add(rect);
        }
    }
}
